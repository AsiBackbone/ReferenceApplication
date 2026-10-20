# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0.402@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa AS build
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

# Package the checked-in EF Core migrations as a self-contained executable. The pinned dotnet-ef version comes from the
# repository tool manifest, and --no-build reuses the Release output produced by publish above.
FROM build AS migrations-build
RUN dotnet tool restore \
    && dotnet tool run dotnet-ef migrations bundle \
        --project src/ReferenceApplication.Infrastructure \
        --startup-project src/ReferenceApplication.Web \
        --context ApplicationDbContext \
        --configuration $BUILD_CONFIGURATION \
        --no-build \
        --output /app/efbundle

# Opt-in, one-shot image that applies migrations to the configured ApplicationDatabase and exits. The application never
# migrates at startup; Docker Compose runs this image before the web service. Build it with --target migrations.
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac AS migrations
WORKDIR /app

ENV DOTNET_RUNNING_IN_CONTAINER=true

# The bundle builds the application's own host to resolve ApplicationDbContext, so it reads the same appsettings and
# environment variables as the web service.
COPY --from=build /app/publish .
COPY --from=migrations-build /app/efbundle ./efbundle

RUN mkdir -p /app/Logs /app/data && chown -R $APP_UID:$APP_UID /app

USER $APP_UID

ENTRYPOINT ["./efbundle"]

# The runtime image stays last so a plain `docker build .` produces it.
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac AS final
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
