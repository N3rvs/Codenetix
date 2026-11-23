# syntax=docker/dockerfile:1
# Imagen del Sandbox (AD-6): un contenedor desechable por Ejecución en el que se ejecuta el código del
# repositorio. Construir desde la raíz del repositorio:
#   docker build -f docker/sandbox.Dockerfile -t codenetix-sandbox .
#
# - SDK de .NET 10 + Node 24 LTS (sin @anthropic-ai/claude-code: se instala en el volumen codenetix-tools, 2.3).
# - /tools (volumen codenetix-tools) es del usuario sandbox para que la provisión inicial pueda escribir en él, y
#   /tools/node_modules/.bin:/tools/bin van primero en el PATH.
# - Usuario no root "sandbox" (uid/gid 1000) y WORKDIR /workspace (volumen codenetix-ws-<runId>).
# - CodeNetix lo arranca con el sistema de archivos raíz en solo lectura, /tmp en tmpfs, sin red y sin
#   capacidades; HOME y la caché de NuGet viven en /tmp.

FROM node:24-bookworm-slim AS node

FROM mcr.microsoft.com/dotnet/sdk:10.0

# Node 24 LTS (binario oficial de la imagen node:24) con npm y npx.
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -sf ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
 && ln -sf ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx \
 && node --version && npm --version

# Usuario sandbox con uid/gid 1000 (si la base ya trae un usuario o grupo 1000, se elimina).
RUN set -eux; \
    existing_user="$(getent passwd 1000 | cut -d: -f1 || true)"; \
    if [ -n "$existing_user" ]; then userdel -r "$existing_user" 2>/dev/null || userdel "$existing_user"; fi; \
    existing_group="$(getent group 1000 | cut -d: -f1 || true)"; \
    if [ -n "$existing_group" ]; then groupdel "$existing_group"; fi; \
    groupadd --gid 1000 sandbox; \
    useradd --uid 1000 --gid 1000 --no-create-home --home-dir /tmp/home --shell /bin/sh sandbox; \
    mkdir -p /workspace /tools; \
    chown sandbox:sandbox /workspace /tools; \
    chmod 0755 /tools

ENV PATH=/tools/node_modules/.bin:/tools/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin \
    HOME=/tmp/home \
    DOTNET_CLI_HOME=/tmp/home \
    NUGET_PACKAGES=/tmp/nuget \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_GENERATE_ASPNET_CERTIFICATE=false \
    NPM_CONFIG_UPDATE_NOTIFIER=false

USER 1000:1000
WORKDIR /workspace

CMD ["sleep", "infinity"]
