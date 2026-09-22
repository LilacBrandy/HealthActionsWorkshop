using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// Create one Azure credential chain for both configuration access and protected health endpoints.
// In Azure, AZURE_CLIENT_ID selects the user-assigned managed identity; locally, developer credentials can be used.
var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    ManagedIdentityClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID")
});

// Retrieve centrally managed HealthTargets settings by using Microsoft Entra authentication.
var appConfigEndpoint = Environment.GetEnvironmentVariable("AZURE_APPCONFIG_ENDPOINT")
    ?? throw new InvalidOperationException("AZURE_APPCONFIG_ENDPOINT is missing.");
var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddAzureAppConfiguration(options => options
        .Connect(new Uri(appConfigEndpoint), credential)
        .Select("HealthTargets*"))
    .Build();

// Require the connection string for the workspace-backed Application Insights resource.
// Supply it through a Container Apps secret reference rather than storing it in source control.
var applicationInsightsConnectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]
    ?? throw new InvalidOperationException("APPLICATIONINSIGHTS_CONNECTION_STRING is missing.");
var serviceName = configuration["OTEL_SERVICE_NAME"] ?? "health-probe";
var telemetryResource = ResourceBuilder.CreateDefault().AddService(serviceName);
using var activitySource = new ActivitySource("HealthProbe");
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(telemetryResource)
    .AddSource(activitySource.Name)
    .AddHttpClientInstrumentation()
    .AddProcessor(new SimpleActivityExportProcessor(
        new AzureMonitorTraceExporter(new AzureMonitorExporterOptions
        {
            ConnectionString = applicationInsightsConnectionString
        })))
    .Build();

// Keep console output for Container Apps diagnostics and also export each structured log record
// through OpenTelemetry to Application Insights, where it can be queried from Log Analytics.
using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.AddOpenTelemetry(logging =>
    {
        logging.IncludeFormattedMessage = true;
        logging.SetResourceBuilder(telemetryResource);
        logging.AddAzureMonitorLogExporter(options =>
            options.ConnectionString = applicationInsightsConnectionString);
    });
});
var logger = loggerFactory.CreateLogger("HealthProbe");

// Use IHttpClientFactory to manage HTTP handlers and apply a consistent five-second timeout.
using var httpClientProvider = new ServiceCollection()
    .AddHttpClient("health-probe", client => client.Timeout = TimeSpan.FromSeconds(5))
    .Services
    .BuildServiceProvider();
var httpClientFactory = httpClientProvider.GetRequiredService<IHttpClientFactory>();

// Require an environment label for every result, then load the endpoints from the selected configuration source.
var environment = configuration["DEPLOYMENT_ENVIRONMENT_NAME"]
    ?? throw new InvalidOperationException("DEPLOYMENT_ENVIRONMENT_NAME is missing.");
var targets = LoadTargets(configuration);

// Probe every configured service. A nonzero process exit code tells the scheduler that at least one probe failed.
var exitCode = 0;
using var probeRun = activitySource.StartActivity("HealthProbeRun", ActivityKind.Internal);
probeRun?.SetTag("deployment.environment.name", environment);
probeRun?.SetTag("health.target.count", targets.Count);

foreach (var target in targets)
{
    var success = await ProbeTargetAsync(target, environment, credential, httpClientFactory, logger, CancellationToken.None);

    if (!success)
    {
        exitCode = 1;
    }
}

return exitCode;

static List<HealthTarget> LoadTargets(IConfiguration configuration)
{
    var serializerOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var targetsJson = configuration["HealthTargets"];
    var targets = string.IsNullOrWhiteSpace(targetsJson)
        ? configuration.GetSection("HealthTargets").Get<List<HealthTarget>>()
        : JsonSerializer.Deserialize<List<HealthTarget>>(targetsJson, serializerOptions);

    // Stop immediately when there is nothing to probe; a silent successful run would be misleading.
    if (targets is not { Count: > 0 })
    {
        throw new InvalidOperationException("No health targets are configured.");
    }

    foreach (var target in targets)
    {
        if (string.IsNullOrWhiteSpace(target.Name)
            || !Uri.TryCreate(target.Url, UriKind.Absolute, out var targetUri)
            || targetUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"Health target '{target.Name}' must have a name and an absolute HTTP(S) URL.");
        }
    }

    return targets;
}

static async Task<bool> ProbeTargetAsync(
    HealthTarget target,
    string environment,
    TokenCredential credential,
    IHttpClientFactory httpClientFactory,
    ILogger logger,
    CancellationToken cancellationToken)
{
    // Measure the complete operation, including token acquisition and the HTTP request.
    var stopwatch = Stopwatch.StartNew();

    try
    {
        // Create a fresh GET request for this target so headers and disposal are isolated per probe.
        using var request = new HttpRequestMessage(HttpMethod.Get, target.Url);

        // Public endpoints need no token. Protected endpoints provide an OAuth scope and receive a bearer token
        // from the same managed identity or local developer credential used for App Configuration.
        if (!string.IsNullOrEmpty(target.Scope))
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext([target.Scope]),
                cancellationToken);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token.Token);
        }

        // Read only the response headers because a health probe needs status and latency, not the response body.
        using var client = httpClientFactory.CreateClient("health-probe");
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        // Emit named fields rather than formatted text so monitoring queries can filter and aggregate each value.
        logger.LogInformation(
            "Health probe {ServiceName} {Environment} {Success} {StatusCode} {DurationMs}",
            target.Name,
            environment,
            response.IsSuccessStatusCode,
            (int)response.StatusCode,
            stopwatch.ElapsedMilliseconds);

        return response.IsSuccessStatusCode;
    }
    // Convert expected authentication, network, and timeout failures into an unhealthy result.
    // Unexpected programming errors remain unhandled so they are visible to the scheduler and operators.
    catch (Exception exception) when (
        exception is AuthenticationFailedException or HttpRequestException or TaskCanceledException)
    {
        logger.LogWarning(
            "Health probe {ServiceName} {Environment} failed {ErrorType} {DurationMs}",
            target.Name,
            environment,
            exception.GetType().Name,
            stopwatch.ElapsedMilliseconds);

        return false;
    }
}

// Each configured target has a display name, an absolute health URL, and an optional OAuth scope.
public sealed record HealthTarget(string Name, string Url, string? Scope);
