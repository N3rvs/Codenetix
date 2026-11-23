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
| `CODENETIX__Polling__Label` | `codenetix` (etiqueta de disparo de la instancia: la usan el sondeo y el webhook de GitHub) |
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

## Webhooks de GitHub

Con un servidor accesible desde GitHub, `codenetix serve` procesa una issue en cuanto se etiqueta, sin esperar al
sondeo. El webhook escucha en el puerto `:5001`, que solo sirve `/webhooks/*` (sin `admin-token`: lo autentica la
firma); el Panel y la API siguen en `:5000`. Está **desactivado por defecto**: se activa al definir el secreto.

| Variable | Por defecto |
| --- | --- |
| `CODENETIX__GitHub__WebhookSecret` | Vacío (webhook desactivado: `POST /webhooks/github` responde `404`). Al menos 16 caracteres |
| `CODENETIX__GitHub__WebhookRepositories` | Vacío (cualquier repositorio que envíe un webhook firmado). Lista `owner/repo` separada por comas (o `__0`, `__1`…) |

1. Publica el puerto de webhooks detrás de tu proxy TLS, p. ej. añadiendo `-p 127.0.0.1:5001:5001` al `docker run` y
   `-e CODENETIX__GitHub__WebhookSecret=<secreto-largo-y-aleatorio>` (por ejemplo, `openssl rand -hex 32`).
2. En GitHub, en el repositorio (o en la organización): *Settings → Webhooks → Add webhook*:
   - **Payload URL:** `https://<tu-host>/webhooks/github`
   - **Content type:** `application/json` (con `application/x-www-form-urlencoded` responde `415`)
   - **Secret:** el mismo valor de `CODENETIX__GitHub__WebhookSecret`
   - **Events:** *Let me select individual events* → **Issues**
3. GitHub envía un `ping` al guardarlo: en *Recent Deliveries* debe verse `200` con `{"result":"pong"}`.

Cómo se comporta:

- Se verifica la firma `X-Hub-Signature-256` (HMAC SHA-256 del cuerpo); si falta o no coincide, `401` y no se procesa.
  En modo diagnóstico responde `503`. Cuerpos de más de 1 MiB: `413`.
- Solo dispara trabajo `issues`/`labeled` con la etiqueta de disparo (`CODENETIX__Polling__Label`, por defecto
  `codenetix`) sobre una issue abierta. Lo demás responde `200` con `{"result":"ignored","reason":"…"}`, visible en
  *Recent Deliveries*.
- La respuesta (`202`, `{"result":"queued"}`) sale al instante; después CodeNetix busca el evento `labeled` en la API
  de GitHub y solo procesa la issue si **quien puso la etiqueta** tiene permiso de escritura (si no, «ignorado: no
  autorizado»).
- Comparte la deduplicación con el sondeo: el mismo etiquetado, llegue por webhook, por sondeo o por los dos, crea un
  único Evento. Reentregar una entrega (*Redeliver*) no duplica nada; para relanzar una issue, quita la etiqueta y
  vuelve a ponerla.
- Las entregas pendientes se guardan en memoria: si CodeNetix se reinicia antes de procesarlas, o GitHub no muestra el
  evento `labeled` tras unos 40 s de reintentos, se descartan con un aviso en el log. Las recupera el sondeo (si el
  repositorio está en `CODENETIX__Polling__Repositories`) o un *Redeliver* desde GitHub. El identificador
  `X-GitHub-Delivery` aparece en los logs y en el Evento.
- `codenetix action` no abre `:5001` ni recibe webhooks.

## Webhooks de Sentry

`codenetix serve` puede recibir los errores nuevos o en regresión de Sentry y trabajar en ellos en el repositorio que
les corresponde. Usa el mismo puerto `:5001` que el webhook de GitHub y está **desactivado por defecto**.

| Variable | Por defecto |
| --- | --- |
| `CODENETIX__Sentry__WebhookSecret` | Vacío (webhook desactivado: `POST /webhooks/sentry` responde `404`). El *Client Secret* de la integración; al menos 16 caracteres |
| `CODENETIX__Sentry__Projects__<slug>` | Ninguno. `owner/repo` del Repositorio objetivo del proyecto de Sentry `<slug>` (hasta 100 proyectos) |

La correspondencia entre proyectos de Sentry y repositorios **solo** se configura en la instancia (variables de entorno
o `/config/codenetix.instance.yml`, con `Sentry: { Projects: { front-end: acme/web } }`), nunca en `codenetix.yml`.
Si el slug lleva guiones y tu shell no admite la variable, pásala con `docker run -e "CODENETIX__Sentry__Projects__front-end=acme/web"`
o usa el archivo YAML.

1. En Sentry: *Settings → Developer Settings → Custom Integrations → Create New Integration → Internal Integration*:
   - **Webhook URL:** `https://<tu-host>/webhooks/sentry`
   - **Permissions:** *Issue & Event* → *Read*
   - **Webhooks:** marca **issue**
2. Copia su **Client Secret** en `CODENETIX__Sentry__WebhookSecret` y añade un `CODENETIX__Sentry__Projects__<slug>` por
   proyecto.
3. El token de GitHub de la instancia necesita permiso para **crear issues** en cada Repositorio objetivo.

Cómo se comporta:

- Se verifica `Sentry-Hook-Signature` (HMAC SHA-256 del cuerpo con el *Client Secret*); si falta o no coincide, `401` y
  no se procesa. En modo diagnóstico, `503`; cuerpos de más de 1 MiB, `413`; contenido que no es JSON, `415`.
- Solo dispara trabajo el recurso `issue` con la acción `created` (incidencia nueva) o `unresolved` por **regresión**
  (`substatus: regressed`, o reabierta por Sentry y no por una persona). El resto responde `200`
  `{"result":"ignored","reason":"…"}`.
- Un proyecto sin correspondencia queda registrado como Evento **ignorado** con el motivo, visible en el Panel
  (*Historial → Eventos ignorados*) y en `GET /api/events/ignored`.
- La respuesta (`202`, `{"result":"queued"}`) sale al instante. Después CodeNetix abre en el Repositorio objetivo una
  **issue de seguimiento** con la etiqueta `codenetix-sentry` (título, origen, nivel y enlace a Sentry, saneados y
  marcados como datos) y crea el Evento sobre ella: la cadena trabaja con esa issue, el PR la cierra y el aviso final
  aparece en ella. La issue se reutiliza en las regresiones y nunca se duplica (se busca por un marcador antes de crearla).
- Deduplicación: `sentry:<issueId>` para la incidencia nueva y `sentry:<issueId>:regression:<n>` para cada regresión;
  N eventos del mismo error generan como mucho una Ejecución en curso (si hay una, el nuevo Evento queda ignorado por
  «objeto ocupado»). Una reentrega con el mismo `Request-ID` no crea nada.
- Autorización: Sentry no tiene un usuario de GitHub. Solo genera trabajo un webhook con firma válida de un proyecto que
  tú has asociado a un repositorio. El texto del error (que puede contener datos de usuarios de tu aplicación) es siempre
  dato para los agentes, nunca instrucciones, y el resultado siempre es un PR que revisa una persona.
- Las entregas pendientes se guardan en memoria (como en GitHub): si CodeNetix se reinicia antes de procesarlas o GitHub
  no responde tras unos 40 s de reintentos, se descartan con un aviso en el log.
- Con `CODENETIX__Sentry__AuthToken` el Analista recibe además la traza del error y `codenetix fix <url-de-sentry>`
  funciona (ver la sección siguiente).

## Contexto de Sentry para el Analista

Con un token de la API de Sentry, el Analista recibe la traza, el contexto y la frecuencia del error de cada Ejecución que
nace de Sentry (webhook o `codenetix fix <url-de-sentry>`), así la Spec apunta a la causa real y no solo al título.

| Variable | Valor por defecto |
|---|---|
| `CODENETIX__Sentry__AuthToken` | Vacío (contexto y `fix <url-de-sentry>` desactivados). Token de la integración interna (o *Auth Token*) con los permisos `event:read` y `project:read` |
| `CODENETIX__Sentry__Organization` | Ninguno; obligatorio con el token. Slug de la organización (`acme` en `https://acme.sentry.io`) |
| `CODENETIX__Sentry__BaseUrl` | `https://sentry.io`. Para Sentry autoalojado, su URL (`https://sentry.example.com`); usa `https` salvo en una red de confianza: el token viaja en `Authorization` |

Valores inválidos (token de menos de 8 caracteres, organización que no es un slug, URL no `http(s)` o con credenciales,
token sin organización) dejan la instancia en modo diagnóstico con el motivo, sin citar el valor.

Qué hace en cada Ejecución que viene de Sentry, justo antes de llamar al Analista:

- Lee la incidencia (`GET /api/0/organizations/<org>/issues/<id>/`: frecuencia, usuarios afectados, primera y última
  vez) y su último evento (`…/events/latest/`), con 10 s por petición y 20 s en total, sin reintentos. El token solo se
  envía a `BaseUrl` y nunca se registra.
- Pasa al Analista, en un bloque de datos (nunca en sus instrucciones), solo esto: incidencia, título, culprit y nivel;
  frecuencia; release y entorno; las etiquetas `environment`, `release`, `dist`, `level`, `handled`, `mechanism`,
  `logger`, `transaction`, `runtime`, `os`, `browser` y `device.family`; hasta 3 excepciones encadenadas con 30 marcos
  en total (primero los de tu código, con la línea de código de cada uno) y las 20 últimas migas. Todo acotado a 16 000
  caracteres.
- **Nunca** envía al LLM la petición HTTP (URL, cabeceras, cookies, query string, cuerpo, variables de entorno), el
  usuario (email, IP, id, nombre), `contexts`, `extra`, las variables locales de los marcos, las rutas absolutas ni el
  `data` de las migas. En el texto que sí pasa, las URLs pierden la query string, los emails se cambian por `[email]`,
  las IP por `[ip]` y los secretos conocidos (tokens y keys de la instancia, patrones `sk-…`, `ghp_…`) se enmascaran.
  Aun así, revisa tu `beforeSend`/*data scrubbing* de Sentry: los mensajes de excepción los escribe tu aplicación.
- Si Sentry no está configurado, no responde, rechaza el token o limita las peticiones, el análisis **sigue sin
  contexto** y la Ejecución muestra el evento `SentryContextUnavailable` con el motivo. Con contexto, `SentryContextAttached`
  (cuántos marcos y migas). Nada de Sentry se guarda en la base de datos.

`codenetix fix <url-de-sentry>` (o el formulario del Panel) acepta `https://sentry.io/organizations/<org>/issues/<id>/`,
`https://<org>.sentry.io/issues/<id>/` y `<tu-sentry>/organizations/<org>/issues/<id>/`. CodeNetix nunca llama al host
de la URL: lee la incidencia en `BaseUrl`, busca su proyecto en `CODENETIX__Sentry__Projects__<slug>`, comprueba que el
token de GitHub de la instancia puede escribir en ese repositorio, reutiliza o crea la issue de seguimiento y crea el
Evento (clave `manual:<url>:<requestId>`, sobre la misma issue que los webhooks: como mucho una Ejecución en curso).
Respuestas: `400` si la URL no lleva un id numérico; `422` si Sentry no está configurado, la URL es de otra organización,
la incidencia no existe o el token no puede verla, o el proyecto no tiene repositorio; `502` si Sentry o GitHub no
responden (el CLI reintenta). Sin permiso de escritura, el Evento queda ignorado («no autorizado») y no se crea la issue.

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

## Documentación de diseño

Antes de programar se escribieron los requisitos y la arquitectura, y cada historia tiene su propia
especificación con el registro de su revisión. Todo está en [`_bmad-output/`](_bmad-output/):

- [`planning-artifacts/`](_bmad-output/planning-artifacts/): producto (PRD), arquitectura y las 7 épicas.
- [`implementation-artifacts/`](_bmad-output/implementation-artifacts/): la especificación de cada una de las 36 historias.

## Créditos, contribución y licencia

- **Créditos:** CodeNetix está inspirado en el trabajo y las ideas de **Eric Risco**.
- **Contribuir:** consulta [CONTRIBUTING.md](CONTRIBUTING.md). Todas las contribuciones requieren firmar el
  *Developer Certificate of Origin* (DCO) con `git commit -s`.
- **Seguridad:** consulta [SECURITY.md](SECURITY.md) para reportar vulnerabilidades de forma privada.
- **Licencia:** distribuido bajo la [licencia Apache 2.0](LICENSE).
