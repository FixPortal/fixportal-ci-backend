# syntax=docker/dockerfile:1
# Build stage: .NET SDK only (the UI is no longer served from this container;
# ci.fixportal.org now redirects to www.fixportal.org/ci).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Copy restore metadata first to maximise layer cache reuse for NuGet restore.
COPY *.slnx ./
COPY nuget.config ./
COPY Directory.Build.props Directory.Packages.props ./
COPY src/FixPortal.Ci.Backend.Api/FixPortal.Ci.Backend.Api.csproj src/FixPortal.Ci.Backend.Api/
# The GitHub Packages token is provided as a BuildKit secret (mounted into the
# restore step's env only) rather than an ARG/build-arg, so it never persists in
# the image layer history. nuget.config reads it via %GITHUB_PACKAGES_TOKEN%.
RUN --mount=type=secret,id=github_packages_token,env=GITHUB_PACKAGES_TOKEN \
    dotnet restore src/FixPortal.Ci.Backend.Api/FixPortal.Ci.Backend.Api.csproj
COPY . .
RUN dotnet publish src/FixPortal.Ci.Backend.Api/FixPortal.Ci.Backend.Api.csproj \
    --configuration Release --output /app --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
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
