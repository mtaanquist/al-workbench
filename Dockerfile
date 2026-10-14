# syntax=docker/dockerfile:1.7

# ---- Build stage ----
# Both base images are pinned by digest so two builds of the same commit produce
# byte-identical layers — the release model in PROJECT.md tags a commit that has
# already passed CI, which a floating tag quietly undermines. Bump the digests
# deliberately (`docker buildx imagetools inspect <image>:<tag>`); the comment
# line directly above each FROM records which tag the digest was resolved from.
# mcr.microsoft.com/dotnet/sdk:10.0
FROM mcr.microsoft.com/dotnet/sdk@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /src

# Restore from the project files alone so the layer survives source edits. The
# app references the gateway contracts project (.design/on-prem-gateway.md), so
# its csproj has to be here too or the restore cannot resolve the reference.
COPY ALDevToolbox/ALDevToolbox.csproj ALDevToolbox/
COPY ALWorkbench.Gateway.Contracts/ALWorkbench.Gateway.Contracts.csproj ALWorkbench.Gateway.Contracts/
RUN dotnet restore ALDevToolbox/ALDevToolbox.csproj

COPY . ./

# Release stamp (issue #604). Empty for local `docker build` and for staging
# images; release.yml passes the git tag and the build date so the running app
# can show its version in the sidebar and link to the matching GitHub Release.
# An unstamped image simply shows no version - see Services/BuildInfo.
ARG RELEASE_VERSION=
ARG RELEASE_DATE=
# The on-premises gateway published for the same release (#1363): its SHA-256 and
# whether it was signed, shown on the Gateways page beside the download link. Empty
# outside release.yml, like the two above - see Services/Gateways/GatewayBuild.
ARG GATEWAY_SHA256=
ARG GATEWAY_SIGNED=
RUN dotnet publish ALDevToolbox/ALDevToolbox.csproj -c Release -o /app /p:UseAppHost=false \
    -p:ReleaseVersion="$RELEASE_VERSION" -p:ReleaseDate="$RELEASE_DATE" \
    -p:GatewaySha256="$GATEWAY_SHA256" -p:GatewaySigned="$GATEWAY_SIGNED"

# ---- Runtime stage ----
# mcr.microsoft.com/dotnet/aspnet:10.0
FROM mcr.microsoft.com/dotnet/aspnet@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94
WORKDIR /app

# curl is needed for the HEALTHCHECK below; the slim aspnet image no longer
# ships it. git is needed by the customer-build pipeline to clone customer
# repositories before compiling (the AL compiler itself is provisioned at
# runtime into a volume, not baked here — see AlCompilerProvisioner).
# postgresql-client-18 supplies pg_dump / pg_restore, which the M18
# BackupService shells out to. pg_dump refuses to dump a server newer than
# itself, so the client major must match the compose db image (postgres:18).
# Debian's default postgresql-client is older than 18, so we install from the
# PGDG apt repo to keep client and server in lockstep.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates gnupg git \
    && install -d /usr/share/postgresql-common/pgdg \
    && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
        -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc \
    && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt $(. /etc/os-release && echo $VERSION_CODENAME)-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client-18 \
    && apt-get purge -y --auto-remove gnupg \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./

# Run as the non-root `app` user the aspnet images ship (uid 1654). This
# container clones customer repositories, provisions and runs the AL compiler
# over that source, and holds the database credentials in its environment, so
# uid 0 is more exposure than the workload needs.
#
# The three mount points are created and chowned here so that Docker seeds a
# freshly created named volume with `app` ownership. An *existing* volume from
# an older (root) install keeps its root ownership and needs a one-off chown —
# see .design/deployment.md.
RUN install -d -o app -g app \
        /var/lib/aldevtoolbox \
        /var/lib/aldevtoolbox/dp-keys \
        /var/lib/aldevtoolbox/backups \
        /var/lib/aldevtoolbox/altool
USER app

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

# /healthz exercises both the Postgres connection and the Data Protection key
# ring; /readyz only flips green once startup work (migrations + bootstrap
# admin) has finished. The container HEALTHCHECK is liveness-oriented, so it
# polls /healthz — a node that loses Postgres or its DP keys should drop out
# of rotation regardless of startup state.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD curl --fail --silent --show-error http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "ALDevToolbox.dll"]
