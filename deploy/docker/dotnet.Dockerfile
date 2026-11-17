# syntax=docker/dockerfile:1
#
# The three .NET images of the deployment (Step 14, ADR-0034), built from one Dockerfile so
# they share a restore and a compile and cannot drift apart:
#
#   --target api       EKA.Api on ASP.NET Core's runtime image
#   --target worker    EKA.Ingestion.Worker on the plain .NET runtime image
#   --target migrator  an EF Core migration bundle, run once per deployment before the others
#
# Build context: the repository root (see .dockerignore). Every base image is pinned to an
# exact version: the SDK to global.json's, the runtimes to Directory.Packages.props'
# MicrosoftRuntimeVersion. A floating tag means two builds of one commit can differ.

ARG SDK_VERSION=10.0.403
ARG RUNTIME_VERSION=10.0.15

# ── restore ────────────────────────────────────────────────────────────────────────────
# Project files and lock files first, sources later: the restore layer is reused for as
# long as no dependency changes, which is most builds.
FROM mcr.microsoft.com/dotnet/sdk:${SDK_VERSION} AS restore
WORKDIR /src

COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY .config/dotnet-tools.json .config/
COPY src/EKA.Domain/*.csproj src/EKA.Domain/packages.lock.json src/EKA.Domain/
COPY src/EKA.Contracts/*.csproj src/EKA.Contracts/packages.lock.json src/EKA.Contracts/
COPY src/EKA.Application/*.csproj src/EKA.Application/packages.lock.json src/EKA.Application/
COPY src/EKA.Infrastructure/*.csproj src/EKA.Infrastructure/packages.lock.json src/EKA.Infrastructure/
COPY src/EKA.AI/*.csproj src/EKA.AI/packages.lock.json src/EKA.AI/
COPY src/EKA.Api/*.csproj src/EKA.Api/packages.lock.json src/EKA.Api/
COPY src/EKA.Ingestion.Worker/*.csproj src/EKA.Ingestion.Worker/packages.lock.json src/EKA.Ingestion.Worker/

# --locked-mode: the lock files are the contract. A package that resolves differently from
# what was committed fails the build here instead of shipping (supply chain, ADR-0006).
RUN dotnet restore src/EKA.Api/EKA.Api.csproj --locked-mode \
 && dotnet restore src/EKA.Ingestion.Worker/EKA.Ingestion.Worker.csproj --locked-mode \
 && dotnet tool restore

# ── build ──────────────────────────────────────────────────────────────────────────────
FROM restore AS build
COPY src/ src/
# ContinuousIntegrationBuild: deterministic paths in the PDBs, no machine-specific bytes.
ARG BUILD_PROPERTIES="-p:ContinuousIntegrationBuild=true"
RUN dotnet publish src/EKA.Api/EKA.Api.csproj -c Release -o /out/api --no-restore ${BUILD_PROPERTIES} \
 && dotnet publish src/EKA.Ingestion.Worker/EKA.Ingestion.Worker.csproj -c Release -o /out/worker --no-restore ${BUILD_PROPERTIES}

# The migration bundle: one executable that applies pending migrations and nothing else.
# Built from the same sources as the API, so schema and code always match. Framework-
# dependent, not self-contained: --self-contained needs a linux-x64 restore that the locked
# restore above does not include (NETSDK1047, found in Step 14), and the startup project is a
# web app, so the bundle runs on the ASP.NET Core runtime image either way.
FROM build AS bundle
RUN dotnet dotnet-ef migrations bundle \
      --project src/EKA.Infrastructure --startup-project src/EKA.Api \
      --configuration Release --output /out/efbundle

# ── api ────────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:${RUNTIME_VERSION} AS api
WORKDIR /app
# The shared upload folder, created owned by the non-root user BEFORE it becomes a volume:
# Docker copies this ownership into a new named volume, so the app can write to it without
# running as root.
RUN mkdir -p /data/files && chown "$APP_UID" /data/files
COPY --from=build /out/api .
# $APP_UID (1654) is the non-root "app" user the .NET images ship. A process that is
# compromised as root in a container is one kernel bug away from the host.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Storage__RootPath=/data/files \
    DOTNET_gcServer=1
EXPOSE 8080
ENTRYPOINT ["dotnet", "EKA.Api.dll"]

# ── worker ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime:${RUNTIME_VERSION} AS worker
WORKDIR /app
RUN mkdir -p /data/files && chown "$APP_UID" /data/files
COPY --from=build /out/worker .
USER $APP_UID
ENV Storage__RootPath=/data/files \
    DOTNET_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "EKA.Ingestion.Worker.dll"]

# ── migrator ───────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:${RUNTIME_VERSION} AS migrator
WORKDIR /app
COPY --from=bundle /out/efbundle .
USER $APP_UID
# The connection string arrives as an environment variable at run time, never at build time,
# so it never becomes part of an image layer.
ENTRYPOINT ["/bin/sh", "-c", "exec ./efbundle --connection \"$ConnectionStrings__EkaDatabase\""]
