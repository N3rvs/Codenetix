# CodeNetix

CodeNetix convierte una issue de GitHub en un Pull Request revisado: analiza la issue y genera la Spec, programa la
corrección dentro de un Sandbox Docker aislado con Egress Gateway, valida la compilación y los tests obligatorios,
revisa el cambio y abre el PR. Se publica bajo la [licencia Apache 2.0](LICENSE); consulta también nuestra
[política de seguridad](SECURITY.md).

## Requisitos

- Docker (Docker Engine 24 o superior, o Docker Desktop) en Linux, macOS o Windows, x64 o arm64.
- Un token de GitHub con permisos sobre el repositorio objetivo.
- Tu propia API key (BYOK) de al menos un Proveedor de modelos (Anthropic, OpenAI o un proveedor local compatible).

## Quickstart (primer PR en menos de 10 minutos)

Las imágenes oficiales multiarquitectura (`linux/amd64` y `linux/arm64`) se publican firmadas en GitHub Container
Registry (`ghcr.io/n3rvs/`) con etiquetas semver fijas (coherentes con `action.yml`; **nunca `:latest`**).

### 1. Descargar la imagen del Sandbox y arrancar CodeNetix

```bash
# 1. Descargar la imagen del Sandbox
docker pull ghcr.io/n3rvs/codenetix-sandbox:v0.1.0

# 2. Arrancar CodeNetix en modo serve (Panel y API solo en localhost)
docker run -d --name codenetix \
  -p 127.0.0.1:5000:5000 \
  -v codenetix-data:/data \
  -v /var/run/docker.sock:/var/run/docker.sock \
  --group-add "$(stat -L -c '%g' /var/run/docker.sock)" \
  -e CODENETIX__GitHub__Token=ghp_... \
  -e CODENETIX__Providers__Anthropic__ApiKey=sk-ant-... \
  -e CODENETIX__Sandbox__Image=ghcr.io/n3rvs/codenetix-sandbox:v0.1.0 \
  ghcr.io/n3rvs/codenetix:v0.1.0 serve
```

- `-v /var/run/docker.sock:/var/run/docker.sock`: sin el socket, CodeNetix arranca en modo diagnóstico ("Docker no
  responde…").
- `--group-add`: CodeNetix no se ejecuta como root; necesita el grupo propietario del socket para poder usarlo. Con
  Docker Desktop (macOS y Windows) el socket pertenece a root: usa `--group-add 0`.

### 2. Comprobar salud y lanzar tu primer PR

```bash
# Comprobar que el Host, Docker y el Sandbox están listos
curl http://127.0.0.1:5000/api/health

# Lanzar la resolución completa de una issue siguiendo el progreso en directo
docker exec codenetix codenetix fix https://github.com/<owner>/<repo>/issues/<número> --follow
```

### 3. Abrir el Panel en vivo

Abre `http://127.0.0.1:5000` en tu navegador. Para iniciar sesión en el Panel (y autenticar llamadas a `/api`), obtén
el token generado automáticamente en `/data/admin.token`:

```bash
docker exec codenetix cat /data/admin.token
```

Si prefieres definir un token fijo desde el arranque, pasa `-e CODENETIX__Host__AccessToken=<tu-token-seguro>` al
comando `docker run`.

### Construcción en local (opcional)

Si prefieres construir las imágenes desde el código fuente, ejecuta desde la raíz del repositorio:

```bash
# Imagen principal de CodeNetix
docker build -f docker/codenetix.Dockerfile -t codenetix .

# Imagen del Sandbox: SDK de .NET 10 y Node 24 LTS con usuario no root `sandbox`
docker build -f docker/sandbox.Dockerfile -t codenetix-sandbox .
```

Al usar imágenes construidas localmente, puedes sustituir `ghcr.io/n3rvs/codenetix:v0.1.0` por `codenetix` y omitir
`CODENETIX__Sandbox__Image` (o apuntar a tu etiqueta local).

## Verificación de firmas (`cosign`) y SBOM

Cada versión publicada en `ghcr.io/n3rvs/` se firma sin clave (*keyless* OIDC de GitHub Actions) con
[Cosign](https://docs.sigstore.dev/cosign/system_config/installation/) e incluye SBOM y *provenance* adjuntos.

Verifica las firmas de ambas imágenes antes de desplegarlas:

```bash
cosign verify \
  --certificate-identity-regexp "^https://github\.com/N3rvs/codenetix/\.github/workflows/release\.yml@refs/tags/v" \
  --certificate-oidc-issuer "https://token.actions.githubusercontent.com" \
  ghcr.io/n3rvs/codenetix:v0.1.0

cosign verify \
  --certificate-identity-regexp "^https://github\.com/N3rvs/codenetix/\.github/workflows/release\.yml@refs/tags/v" \
  --certificate-oidc-issuer "https://token.actions.githubusercontent.com" \
  ghcr.io/n3rvs/codenetix-sandbox:v0.1.0
```

Para inspeccionar el SBOM (SPDX) adjunto a cada imagen multiarquitectura:

```bash
docker buildx imagetools inspect ghcr.io/n3rvs/codenetix:v0.1.0 --format '{{ json .SBOM }}'
docker buildx imagetools inspect ghcr.io/n3rvs/codenetix-sandbox:v0.1.0 --format '{{ json .SBOM }}'
```

## Seguridad, riesgos y uso de keys propias (BYOK)

- **Riesgo del socket de Docker:** montar `/var/run/docker.sock` equivale a conceder control `root` sobre el motor
  Docker del host. CodeNetix solo utiliza el socket para crear y destruir sus propios Sandboxes (etiquetados
  `codenetix.managed=true`), sin capacidades, sin *bind mounts* y con límites de CPU, memoria y procesos. Aun así,
  ejecuta CodeNetix en un host o máquina virtual dedicada y nunca expongas el Panel fuera de `127.0.0.1` sin TLS y sin
  configurar `CODENETIX__Host__AccessToken`.
- **Modelo de seguridad del Sandbox:** el agente que escribe y ejecuta código corre aislado dentro de un contenedor
  desechable sin directorio `.git`, sin credenciales ni secretos en el entorno, con sistema de archivos raíz de solo
  lectura, `/tmp` en memoria, `no-new-privileges`, usuario `1000:1000` y conectado únicamente a la red Docker
  `codenetix-sandbox` (`internal: true`, sin salida directa a internet). Toda comunicación externa pasa por el **Egress
  Gateway** (`gateway:8080/llm` para el Proveedor de modelos y `gateway:8081` como proxy `CONNECT` restringido a la
  allowlist de NuGet; cualquier otro destino recibe `403` y emite `EgressBlocked`). Este aislamiento se verifica en CI
  mediante la prueba de fuego de seguridad (Historia 2.5, `Category=SecurityFire`).
- **Uso de keys propias (BYOK) y presupuesto:** CodeNetix funciona con tus propias claves (*Bring Your Own Key*) de
  Anthropic (`CODENETIX__Providers__Anthropic__ApiKey`), OpenAI (`CODENETIX__Providers__OpenAI__ApiKey`) o proveedores
  locales compatibles. Las claves reales permanecen en el Host, nunca entran al Sandbox y pasan por un único
  `ISecretRedactor` para no persistirse ni imprimirse en logs. Puedes controlar el gasto máximo por Ejecución con
  `CODENETIX__Limits__BudgetUsd` o en `codenetix.yml`, ajustar tarifas con `CODENETIX__Pricing__*` y confiar en el
  corte automático al agotar presupuesto (respuesta HTTP `402` desde el Egress Gateway + límite `--max-budget-usd` en el
  agente).
- **Divulgación de vulnerabilidades:** consulta [SECURITY.md](SECURITY.md) para reportar problemas de seguridad de
  forma privada.

## El Sandbox de cada Ejecución

| Qué | Cómo |
| --- | --- |
| Contenedor | `codenetix-sbx-<shortId>`, etiquetas `codenetix.run=<runId>` y `codenetix.managed=true` |
| Árbol de trabajo | Volumen `codenetix-ws-<runId>` en `/workspace`: solo los archivos versionados del commit base, nunca `.git` ni credenciales |
| Herramientas | Volumen `codenetix-tools` en solo lectura en `/tools` (se conserva entre Ejecuciones) |
| Red y Egress Gateway | Red Docker `codenetix-sandbox` (`internal: true`, sin salida directa a internet). El Sandbox solo alcanza `gateway`: `:8080/llm` (proxy inverso YARP hacia el Proveedor de `Coder`, sustituyendo el token efímero `cnx-run-<runId>-<nonce>` por la credencial real y registrando `TokensConsumed`) y `:8081` (proxy `CONNECT` TCP restringido a la allowlist de NuGet; cualquier otro destino devuelve `403` y emite `EgressBlocked`) |
| Aislamiento | Sistema de archivos raíz de solo lectura, `/tmp` en memoria, sin capacidades, `no-new-privileges`, usuario 1000:1000 |
| Ciclo de vida | Se crea tras clonar el repositorio y se destruye (contenedor, volumen de trabajo y token del Gateway) al terminar la Ejecución, en cualquier Estado; al cancelarse se detiene en 10 s como máximo |

Variables (todas opcionales):

| Variable | Por defecto |
| --- | --- |
| `CODENETIX__Sandbox__Image` | `codenetix-sandbox:latest` |
| `CODENETIX__Sandbox__DockerEndpoint` | `DOCKER_HOST` o `unix:///var/run/docker.sock` (`npipe://./pipe/docker_engine` en Windows) |
| `CODENETIX__Sandbox__Cpus` | `2` |
| `CODENETIX__Sandbox__MemoryMb` | `4096` |
| `CODENETIX__Sandbox__PidsLimit` | `512` |
| `CODENETIX__Sandbox__MaxLifetimeMinutes` | `60` |
| `CODENETIX__Sandbox__NetworkName` | `codenetix-sandbox` |
| `CODENETIX__Sandbox__HostContainer` | Auto-detectado en contenedor (`/.dockerenv`); vacío si el host corre fuera de Docker |
| `CODENETIX__Sandbox__LlmGatewayPort` | `8080` |
| `CODENETIX__Sandbox__ConnectProxyPort` | `8081` |
| `CODENETIX__Sandbox__AllowedNuGetHosts` | `api.nuget.org:443` |
| `CODENETIX__Agents__Coder__Provider` | Único Proveedor configurado o `Anthropic` |
| `CODENETIX__Agents__Coder__Model` | `claude-sonnet-5-5` |

## Sondeo de issues etiquetadas

Sin servidor público ni webhooks, `codenetix serve` puede recoger solo las issues abiertas con la etiqueta `codenetix`
de los repositorios que le indiques. Está **desactivado por defecto**: se activa al definir los repositorios.

| Variable | Por defecto |
| --- | --- |
| `CODENETIX__Polling__Repositories` | Vacío (sondeo desactivado). Lista `owner/repo` separada por comas, p. ej. `acme/api,acme/web` (o `__0`, `__1`…) |
| `CODENETIX__Polling__Label` | `codenetix` |
| `CODENETIX__Polling__IntervalSeconds` | `300` (5 minutos; entre `60` y `86400`) |

- Cada ciclo busca, en cada issue abierta con la etiqueta, quién la etiquetó por última vez. Solo se procesa si esa
  persona tiene permiso de escritura en el repositorio; si no, el Evento queda «ignorado: no autorizado».
- Cada etiquetado se procesa una sola vez: para relanzar una issue, quita la etiqueta y vuelve a ponerla. Si ya la
  lanzaste con `codenetix fix` después de etiquetarla, el sondeo no la repite.
- Al activarlo se recogen también las issues que ya llevaban la etiqueta.
- Si renombras o transfieres un repositorio y actualizas `CODENETIX__Polling__Repositories` con el nombre nuevo, las
  issues abiertas que ya llevaban la etiqueta se vuelven a lanzar: la deduplicación va por `owner/repo` y el nombre
  nuevo es, para CodeNetix, otro repositorio. Quita antes la etiqueta de las que no quieras relanzar.
- Usa peticiones condicionales (`ETag`) para gastar poco rate limit del token y, si GitHub lo agota, espera a que se
  renueve. En modo diagnóstico, y en `codenetix action`, no sondea.

## Modo GitHub Action (`codenetix action`)

Dentro de un job de GitHub Actions (runner `ubuntu-*` con Docker), `codenetix action` procesa una issue al etiquetarla
con `codenetix`: lee `GITHUB_EVENT_NAME`/`GITHUB_EVENT_PATH`, autoriza a quien puso la etiqueta (permiso de escritura),
ejecuta la misma cadena que `serve` (Sandbox y Egress Gateway incluidos) sobre una SQLite temporal y vuelca los eventos
de la Ejecución a la salida del job. No abre el Panel ni los puertos `:5000`/`:5001`. Sale con `0` (PR creado o nada que
hacer), `1` (Ejecución fallida, actor sin permiso o error) o `2` (uso).

### Workflow de ejemplo

Copia [examples/workflows/codenetix.yml](examples/workflows/codenetix.yml) en `.github/workflows/codenetix.yml` de tu
repositorio y configura el *secret* `ANTHROPIC_API_KEY` (o `OPENAI_API_KEY`):

```yaml
name: CodeNetix

on:
  issues:
    types: [labeled]

permissions:
  contents: write
  pull-requests: write
  issues: write

jobs:
  codenetix:
    if: github.event.label.name == 'codenetix'
    runs-on: ubuntu-latest
    timeout-minutes: 60
    steps:
      - name: Ejecutar CodeNetix
        uses: n3rvs/codenetix@v0.1.0
        with:
          github-token: ${{ secrets.GITHUB_TOKEN }}
          anthropic-api-key: ${{ secrets.ANTHROPIC_API_KEY }}
          # openai-api-key: ${{ secrets.OPENAI_API_KEY }}
          # budget-usd: '2.00'
```

### Inputs de `action.yml`

| Input | Requerido | Por defecto | Descripción |
| --- | --- | --- | --- |
| `version` | No | `v0.1.0` | Etiqueta semver de `ghcr.io/n3rvs/codenetix:<tag>` y `ghcr.io/n3rvs/codenetix-sandbox:<tag>` (nunca `latest`) |
| `github-token` | No | `${{ github.token }}` | Token de GitHub con permisos de escritura en `contents`, `pull-requests` e `issues` |
| `anthropic-api-key` | No | `''` | API key de Anthropic desde los *secrets* del repositorio |
| `openai-api-key` | No | `''` | API key de OpenAI desde los *secrets* del repositorio |
| `label` | No | `codenetix` | Etiqueta de issue que activa CodeNetix |
| `budget-usd` | No | `''` | Límite máximo de presupuesto por Ejecución en USD |
| `image` | No | `''` | Override opcional de la imagen principal de CodeNetix |
| `sandbox-image` | No | `''` | Override opcional de la imagen del Sandbox Docker |

### Permisos de `GITHUB_TOKEN` y avisos de seguridad

- **Permisos necesarios:** el workflow necesita `contents: write`, `pull-requests: write` e `issues: write`. Además, en
  *Settings → Actions → General* del repositorio debes activar **«Allow GitHub Actions to create and approve pull requests»**
  para que `GITHUB_TOKEN` pueda abrir Pull Requests.
- **Aviso sobre forks y *secrets*:** los *secrets* del repositorio no llegan a workflows disparados desde forks. La v1
  solo se dispara mediante `issues` (`types: [labeled]`) y nunca debe configurarse con `pull_request_target` ni
  `workflow_run`.
- **Aviso sobre Pull Requests creados con `GITHUB_TOKEN`:** los PR creados con `GITHUB_TOKEN` no disparan
  automáticamente otros workflows de CI (comportamiento de seguridad de GitHub para evitar bucles infinitos).

### Variables de entorno de `codenetix action`

| Variable | Por defecto |
| --- | --- |
| `CODENETIX__GitHub__Token` | `GITHUB_TOKEN` (necesita `contents`, `pull-requests` e `issues` de escritura; los PR que crea no disparan workflows) |
| `CODENETIX__GitHub__Login` | `github-actions[bot]` con `GITHUB_TOKEN` |
| `CODENETIX__GitHub__ApiUrl` / `CloneBaseUrl` | `GITHUB_API_URL` / `GITHUB_SERVER_URL` |
| `CODENETIX__Action__Label` | `codenetix` |
| `CODENETIX__Persistence__DataDirectory` | Directorio temporal bajo `RUNNER_TEMP`, borrado al terminar |

## Desarrollo

```bash
dotnet build CodeNetix.slnx
dotnet test --solution CodeNetix.slnx
```

Las pruebas de integración del Sandbox usan el Docker local (Docker Desktop en Windows) y la imagen
`codenetix-sandbox`; si Docker no responde o falta la imagen, se omiten. Todo lo que crean lleva la etiqueta
`codenetix.managed=true` y se borra al terminar. La prueba de fuego de seguridad (repositorio hostil sin acceso a
`.git/config`, credenciales reales ni salida externa no autorizada) forma parte de la suite estándar y se puede lanzar
por separado con:

```bash
dotnet test --project tests/CodeNetix.Host.Tests -- --filter-trait Category=SecurityFire
```

## Créditos, contribución y licencia

- **Créditos:** CodeNetix está inspirado en el trabajo y las ideas de **Eric Risco**.
- **Contribuir:** consulta [CONTRIBUTING.md](CONTRIBUTING.md). Todas las contribuciones requieren firmar el
  *Developer Certificate of Origin* (DCO) con `git commit -s`.
- **Seguridad:** consulta [SECURITY.md](SECURITY.md) para reportar vulnerabilidades de forma privada.
- **Licencia:** distribuido bajo la [licencia Apache 2.0](LICENSE).
