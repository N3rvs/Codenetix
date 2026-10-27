# syntax=docker/dockerfile:1
# Imagen de CodeNetix (modo serve). Construir desde la raíz del repositorio:
#   docker build -f docker/codenetix.Dockerfile -t codenetix .

# --- Panel (React + Vite) -> src/CodeNetix.Host/wwwroot ---------------------
FROM node:24 AS panel
WORKDIR /src/panel
COPY panel/package.json panel/package-lock.json ./
RUN npm ci
COPY panel/ ./
RUN npm run build

# --- .NET: restore, ArchitectureTests, publish --------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
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
RUN dotnet restore src/CodeNetix.Host/CodeNetix.Host.csproj \
 && dotnet restore tests/CodeNetix.ArchitectureTests/CodeNetix.ArchitectureTests.csproj

COPY src/ src/
COPY tests/CodeNetix.ArchitectureTests/ tests/CodeNetix.ArchitectureTests/

# Una referencia prohibida entre módulos (AD-1) rompe la imagen.
RUN dotnet test --project tests/CodeNetix.ArchitectureTests/CodeNetix.ArchitectureTests.csproj -c Release --no-restore

COPY --from=panel /src/src/CodeNetix.Host/wwwroot/ src/CodeNetix.Host/wwwroot/
RUN test -f src/CodeNetix.Host/wwwroot/index.html \
 && dotnet publish src/CodeNetix.Host/CodeNetix.Host.csproj -c Release --no-restore -o /app

# --- Runtime -------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
RUN ln -s /app/codenetix /usr/local/bin/codenetix \
 && mkdir -p /data \
 && chown app:app /data

ENV DOTNET_ROOT=/usr/share/dotnet
USER app
VOLUME /data
EXPOSE 5000

HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=3 CMD ["codenetix", "healthcheck"]

ENTRYPOINT ["codenetix"]
CMD ["serve"]
