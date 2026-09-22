FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY HealthProbe.csproj .
RUN dotnet restore HealthProbe.csproj

COPY . .
RUN dotnet publish HealthProbe.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app .

USER $APP_UID
ENTRYPOINT ["dotnet", "HealthProbe.dll"]
