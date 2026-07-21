# Build stage: .NET SDK only (the UI is no longer served from this container;
# ci.fixportal.org now redirects to www.fixportal.org/ci).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Copy restore metadata first to maximise layer cache reuse for NuGet restore.
COPY *.slnx ./
COPY nuget.config ./
COPY Directory.Build.props Directory.Packages.props ./
COPY src/FixPortal.Ci.Backend.Api/FixPortal.Ci.Backend.Api.csproj src/FixPortal.Ci.Backend.Api/
RUN --mount=type=secret,id=github-packages-token,required=true \
    GITHUB_PACKAGES_TOKEN="$(cat /run/secrets/github-packages-token)" \
    dotnet restore src/FixPortal.Ci.Backend.Api/FixPortal.Ci.Backend.Api.csproj
COPY . .
RUN dotnet publish src/FixPortal.Ci.Backend.Api/FixPortal.Ci.Backend.Api.csproj \
    --configuration Release --output /app --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
LABEL org.opencontainers.image.source="https://github.com/FixPortal/fixportal-ci-backend"
WORKDIR /app
# Metrics: the background worker shallow-clones each repo and runs Lizard.
# Install Lizard in an isolated virtual environment (PEP 668-safe).
RUN apt-get update \
    && apt-get install -y --no-install-recommends git python3 python3-pip python3-venv \
    && python3 -m venv /opt/venv \
    && /opt/venv/bin/pip install --no-cache-dir lizard==1.22.2 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
# Writable, app-owned dir for the snapshot file (overrides the appsettings default).
# $APP_UID is the non-root user shipped in the .NET runtime images.
RUN mkdir -p /app/data && chown -R $APP_UID:0 /app/data
ENV ASPNETCORE_HTTP_PORTS=8080
ENV Dashboard__SnapshotPath=/app/data/dashboard-snapshot.json
ENV PATH="/opt/venv/bin:${PATH}"
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "FixPortal.Ci.Backend.Api.dll"]
