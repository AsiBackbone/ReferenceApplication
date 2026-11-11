# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["global.json", "./"]
COPY ["Directory.Build.props", "./"]
COPY ["Directory.Packages.props", "./"]
COPY ["src/ReferenceApplication.Infrastructure/ReferenceApplication.Infrastructure.csproj", "src/ReferenceApplication.Infrastructure/"]
COPY ["src/ReferenceApplication.Infrastructure/packages.lock.json", "src/ReferenceApplication.Infrastructure/"]
COPY ["src/ReferenceApplication.Web/ReferenceApplication.Web.csproj", "src/ReferenceApplication.Web/"]
COPY ["src/ReferenceApplication.Web/packages.lock.json", "src/ReferenceApplication.Web/"]

RUN dotnet restore "src/ReferenceApplication.Web/ReferenceApplication.Web.csproj" --locked-mode

COPY . .

RUN dotnet publish "src/ReferenceApplication.Web/ReferenceApplication.Web.csproj" \
    --configuration $BUILD_CONFIGURATION \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false \
    /p:ContinuousIntegrationBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

COPY --from=build /app/publish .

RUN mkdir -p /app/Logs /app/data /app/data-protection-keys && chown -R $APP_UID:$APP_UID /app

USER $APP_UID

# Health probing is delegated to Docker Compose, Kubernetes, load balancers,
# or hosting infrastructure. Use /health/live and /health/ready.
HEALTHCHECK NONE

ENTRYPOINT ["dotnet", "ReferenceApplication.Web.dll"]
