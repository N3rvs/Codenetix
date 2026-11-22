# syntax=docker/dockerfile:1
# Imagen de CodeNetix (modo serve). Construir desde la raíz del repositorio:
#   docker build -f docker/codenetix.Dockerfile -t codenetix .

# --- Panel (React + Vite) -> src/CodeNetix.Host/wwwroot ---------------------
FROM --platform=$BUILDPLATFORM node:24 AS panel
WORKDIR /src/panel
COPY panel/package.json panel/package-lock.json ./
RUN npm ci
COPY panel/ ./
RUN npm run build

# --- .NET: restore, ArchitectureTests, publish --------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json CodeNetix.slnx Directory.Build.props Directory.Packages.props ./
COPY src/CodeNetix.Contracts/CodeNetix.Contracts.csproj src/CodeNetix.Contracts/
COPY src/CodeNetix.Host/CodeNetix.Host.csproj src/CodeNetix.Host/
COPY src/CodeNetix.Ingestion/CodeNetix.Ingestion.csproj src/CodeNetix.Ingestion/
COPY src/CodeNetix.Pipeline/CodeNetix.Pipeline.csproj src/CodeNetix.Pipeline/
COPY src/CodeNetix.Agents/CodeNetix.Agents.csproj src/CodeNetix.Agents/
COPY src/CodeNetix.Sandbox/CodeNetix.Sandbox.csproj src/CodeNetix.Sandbox/
COPY src/CodeNetix.GitHub/CodeNetix.GitHub.csproj src/CodeNetix.GitHub/
COPY src/CodeNetix.Persistence/CodeNetix.Persistence.csproj src/CodeNetix.Persistence/
COPY tests/CodeNetix.ArchitectureTests/CodeNetix.ArchitectureTests.csproj tests/CodeNetix.ArchitectureTests/
COPY tests/CodeNetix.Host.Tests/CodeNetix.Host.Tests.csproj tests/CodeNetix.Host.Tests/
RUN dotnet restore src/CodeNetix.Host/CodeNetix.Host.csproj \
 && dotnet restore tests/CodeNetix.ArchitectureTests/CodeNetix.ArchitectureTests.csproj \
 && dotnet restore tests/CodeNetix.Host.Tests/CodeNetix.Host.Tests.csproj

COPY schemas/ schemas/
# Instantánea del contrato OpenAPI (AD-14): OpenApiContractTests la compara con el /openapi/v1.json del Host.
COPY panel/openapi/v1.json panel/openapi/v1.json
# Artefactos de la acción composite (Historia 6.2) y documentación de lanzamiento (Historia 6.5):
# CompositeActionTests y ReleaseDocumentationTests los validan.
COPY action.yml README.md ./
COPY SECURITY.md LICENSE CONTRIBUTING.md ./
COPY examples/workflows/codenetix.yml examples/workflows/codenetix.yml
# Workflow de CI (Historia 6.3): CiWorkflowTests valida su contrato y este Dockerfile.
COPY .github/workflows/ci.yml .github/workflows/ci.yml
COPY docker/codenetix.Dockerfile docker/codenetix.Dockerfile
# Workflow de release y Renovate (Historia 6.4): ReleaseWorkflowTests valida ambos artefactos.
COPY .github/workflows/release.yml .github/workflows/release.yml
COPY renovate.json renovate.json
COPY src/ src/
COPY tests/CodeNetix.ArchitectureTests/ tests/CodeNetix.ArchitectureTests/
COPY tests/CodeNetix.Host.Tests/ tests/CodeNetix.Host.Tests/

# Una referencia prohibida entre módulos (AD-1) o un test de Host en rojo (p. ej. los permisos 0600
# de admin.token, que solo se comprueban fuera de Windows) rompen la imagen. Los tests de clonado
# necesitan git (la imagen sdk ya lo trae; se comprueba para fallar con un mensaje claro).
RUN git --version \
 && dotnet test --project tests/CodeNetix.ArchitectureTests/CodeNetix.ArchitectureTests.csproj -c Release --no-restore \
 && dotnet test --project tests/CodeNetix.Host.Tests/CodeNetix.Host.Tests.csproj -c Release --no-restore

ARG TARGETARCH
COPY --from=panel /src/src/CodeNetix.Host/wwwroot/ src/CodeNetix.Host/wwwroot/
RUN test -f src/CodeNetix.Host/wwwroot/index.html \
 && DOTNET_ARCH=$([ "$TARGETARCH" = "arm64" ] && echo "arm64" || echo "x64") \
 && dotnet publish src/CodeNetix.Host/CodeNetix.Host.csproj -c Release -a "$DOTNET_ARCH" --no-self-contained -o /app

# --- Runtime -------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# git para clonar los repositorios (AD-16 paso 1); ca-certificates para https://github.com.
RUN apt-get update \
 && apt-get install -y --no-install-recommends git ca-certificates \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
RUN ln -s /app/codenetix /usr/local/bin/codenetix \
 && mkdir -p /data \
 && chown app:app /data

ENV DOTNET_ROOT=/usr/share/dotnet
USER app
VOLUME /data
# :5000 Panel, /api y /hubs (con admin-token); :5001 solo /webhooks/*.
EXPOSE 5000 5001

HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=3 CMD ["codenetix", "healthcheck"]

ENTRYPOINT ["codenetix"]
CMD ["serve"]
