---
status: final
stepsCompleted: [1, 2, 3, 4]
requirementsConfirmed: true
inputDocuments:
  - _bmad-output/planning-artifacts/prds/prd-Codenetix-2026-10-26/prd.md
  - _bmad-output/planning-artifacts/architecture/architecture-Codenetix-2026-10-26/ARCHITECTURE-SPINE.md
  - _bmad-output/planning-artifacts/briefs/brief-Codenetix-2026-10-26/brief.md
  - _bmad-output/planning-artifacts/briefs/brief-Codenetix-2026-10-26/addendum.md
---

# CodeNetix - Desglose en épicas

## Visión general

Este documento desglosa en épicas e historias implementables los requisitos del PRD y las decisiones de arquitectura (AD-1 a AD-18) de CodeNetix. Todavía no existe un documento de UX: las historias del Panel llevan la nota "pendiente de diseño visual (Sally)".

## Inventario de requisitos

### Requisitos funcionales

- **FR1** (P0): arrancar con un único `docker run`; Panel en localhost en menos de 60 s; sin registro ni login; almacenamiento local que se crea solo.
- **FR2** (P0): configurar keys, tokens y secretos por variables de entorno; enmascararlos siempre; modo diagnóstico si falta alguna variable obligatoria.
- **FR3** (P0): leer `codenetix.yml` (Rutas protegidas, Revisores, modelos y límites), con valores por defecto si no existe. Si no es válido, la Ejecución acaba en `Fallida` sin llamar a ningún agente.
- **FR4** (P0): solo localhost por defecto. Si la instancia se expone, el Panel y la API exigen token; los webhooks quedan exentos del token, pero exigen firma.
- **FR5** (P0): `codenetix fix <URL>` (issue de GitHub o incidencia de Sentry) crea la Ejecución, que aparece en el Panel en menos de 2 s. Una URL no admitida devuelve un error.
- **FR6** (P1): sondeo de las issues con la etiqueta `codenetix`, cada 5 minutos por defecto; nada de duplicados salvo si se reetiqueta.
- **FR7** (P1): webhooks de GitHub y Sentry con firma verificada; respuesta 2xx en menos de 1 s; procesamiento asíncrono; deduplicación.
- **FR8** (P1): correspondencia de cada proyecto de Sentry con un repositorio, definida en la Configuración de instancia; los Eventos sin correspondencia quedan como `ignorado` con su motivo.
- **FR9** (P0): el Analista genera la Spec con un esquema estructurado (máximo 2 reintentos); la Spec se ve en el Panel y va en el PR.
- **FR10** (P0): el Analista puede marcar un Evento como "no automatizable"; la Ejecución acaba en `Fallida` y se comenta el motivo en la issue.
- **FR11** (P0): el Programador trabaja en un Sandbox aislado y efímero, en la rama `codenetix/<id>`; el Sandbox se destruye siempre al terminar.
- **FR12** (P0): al menos un test por cambio; `dotnet build` y `dotnet test` deben pasar (3 reintentos); si no, la Ejecución acaba en `Fallida`.
- **FR13** (P1): presupuesto máximo por Ejecución; al superarlo, `Fallida` con el motivo "presupuesto agotado".
- **FR14** (P0): Revisores independientes (`seguridad`, `calidad`, `tests`) que solo ven la Spec, el diff y los tests. Pueden usar otro Proveedor, incluidos modelos locales compatibles con OpenAI.
- **FR15** (P0): Veredicto estructurado con hallazgos por archivo y línea; si el esquema es inválido, se reintenta y cuenta como `cambios solicitados`.
- **FR16** (P0): hasta 2 Rondas de revisión; un rechazo, o agotar las rondas, lleva a `Fallida`.
- **FR17** (P0): PR con la etiqueta `codenetix` y traza completa (Evento, Spec, Veredictos, tests, riesgo, coste); nunca merge ni push a la rama por defecto.
- **FR18** (P1): clasificación de riesgo determinista (`menor` / `requiere atención`) según Rutas protegidas, tamaño del diff y unanimidad.
- **FR19** (P1): comentario en la issue de origen con el resultado.
- **FR20** (P0): Panel con el grafo de Etapas y el nodo activo; actualización en menos de 1 s.
- **FR21** (P0): terminal en vivo de la Etapa activa, con los secretos enmascarados.
- **FR22** (P1): historial de Ejecuciones (Estado, Spec, Veredictos, PR, duración, coste), persistente.
- **FR23** (P1): cancelar una Ejecución activa; el Sandbox se destruye y no se crea ningún PR.
- **FR24** (P2): al reiniciar, las Ejecuciones activas pasan a `Fallida` ("interrumpida"); los Eventos aceptados no se pierden.
- **FR25** (P2): modo GitHub Action disparado por una issue etiquetada, con los *secrets* del repositorio y sin Panel; usa el mismo Sandbox Docker en un runner `ubuntu-*`.
- **FR26** (P0): solo se procesan los Eventos de actores con permiso de escritura; el texto del Evento siempre se trata como datos.
- **FR27** (P0): el Sandbox solo puede conectarse a NuGet, a GitHub (para el clonado) y al Proveedor; las keys y el token no son legibles por el código del repositorio.

### Requisitos no funcionales

- **NFR1:** el código generado o del repositorio solo se ejecuta en el Sandbox, sin acceso al host ni a los secretos.
- **NFR2:** privacidad: el código solo sale hacia GitHub y los Proveedores elegidos; sin telemetría.
- **NFR3:** coste cero para el mantenedor: nada alojado por el proyecto y ninguna key por defecto.
- **NFR4:** portabilidad: Linux, macOS y Windows (Docker Desktop), en x64 y arm64.
- **NFR5:** observabilidad: inicio, fin, duración, tokens y errores de cada Etapa, consultables en el Panel.
- **NFR6:** Política de merge determinista y cubierta por tests unitarios.

### Requisitos adicionales (de la arquitectura)

- **Sin starter:** solución .NET 10 con los proyectos de AD-1 (Contracts, Host, Ingestion, Pipeline, Agents, Sandbox, GitHub, Persistence), `ArchitectureTests` y `panel/` con React 19 + Vite 8. Es la primera historia de la Épica 1.
- **AD-2:** máquina de estados `PipelineRun` con `FailureReason`; la cancelación es una petición (`IRunControl`); `Decidiendo` no se puede cancelar.
- **AD-3:** `IRunEventSink` único, con `seq`, lotes de 250 ms, publicación tras el commit, límite de 5 MB por Etapa y `afterSeq`.
- **AD-4:** `EventEnvelope`, claves de deduplicación por Fuente, `POST /api/events/manual`; solo el Dispatcher crea Ejecuciones.
- **AD-5:** `IReasoningAgent` (`IChatClient`, salida estructurada, decorador de reintentos, alternativa de JSON en el prompt) e `ICodingAgent` (`claude --bare -p … --permission-mode acceptEdits …`).
- **AD-6:** Sandbox gestionado por Pipeline, volumen con nombre `codenetix-ws-<runId>`, sin `.git`, `ITestRunner`, parada en 10 s como máximo.
- **AD-7:** Egress Gateway con YARP (`/llm`, sustitución de la key ficticia por la real, SSE sin buffer, uso de tokens) + proxy `CONNECT` para NuGet; red `internal` con el alias `gateway`.
- **AD-8:** el módulo GitHub hace el clonado con `extraHeader`, calcula el diff contra `BaseSha`, hace un único commit, push y PR, aplica el guardián y registra las notificaciones idempotentes (`gh_notifications`).
- **AD-9:** `MergePolicy` como función pura con tests de tabla.
- **AD-10:** Configuración de instancia; `codenetix.yml` validado contra el JSON Schema `schemas/codenetix.v1.json`, leído una sola vez desde `BaseSha`; `EffectiveConfig` congelada por Ejecución.
- **AD-11:** un `DbContext` por módulo con prefijos `ing_`, `run_` y `gh_`; WAL.
- **AD-12:** modos `serve`, `fix` y `action`; `action.yml` como acción `composite`.
- **AD-13:** `ISecretRedactor` en el sumidero.
- **AD-14:** unión discriminada de `RunEvent` publicada en el OpenAPI; tipos TS generados.
- **AD-15:** `IBudgetLedger`, middleware de `IChatClient`, respuesta 402 del Gateway y `--max-budget-usd`.
- **AD-16:** secuencia canónica del orquestador.
- **AD-17:** puertos `:5000` (Panel, API, hub) y `:5001` (webhooks); `admin-token` en `/data`; `Exposed` exige un `AccessToken`.
- **AD-18:** `StartupReconciler` en orden fijo, `IInstanceHealth` (modo diagnóstico, 503) y `/api/health`.
- **Release:** CI en GitHub Actions (build, ArchitectureTests, tests de integración con Docker, buildx multiarquitectura), etiquetas semver, cosign + SBOM, Dependabot, `SECURITY.md` y README con el riesgo del socket de Docker.

### Requisitos de diseño UX

Todavía no hay documento de UX. Las historias del Panel (FR20 a FR23) se diseñarán con Sally (`bmad-ux`) antes de implementarse.

### Mapa de cobertura de FR

- FR1, FR2, FR3, FR4, FR5 → Épica 1 (instalación, configuración, acceso, comando directo)
- FR9, FR10 → Épica 1 (Spec y rechazo temprano)
- FR26 → Épica 1 (autorización del disparador)
- FR11, FR12, FR27 → Épica 2 (Sandbox, tests, red y secretos)
- FR14, FR15, FR16 → Épica 3 (Revisores, Veredictos, Rondas)
- FR17, FR18, FR19 → Épica 3 (PR, clasificación de riesgo, notificación)
- FR20, FR21, FR22, FR23 → Épica 4 (Panel en vivo, historial, cancelación)
- FR13, FR24 → Épica 5 (presupuesto, reinicios)
- FR25 → Épica 6 (GitHub Action) + publicación
- FR6, FR7, FR8 → Épica 7 (sondeo, webhooks, Sentry)

## Lista de épicas

Orden aprobado por Longu siguiendo la recomendación de Mary: lanzar pronto una *preview*, siempre con control de coste.

### Épica 1: Instalar y obtener una Spec
El usuario arranca CodeNetix con un único comando, lanza `codenetix fix <issue>` y obtiene una Spec técnica de su issue, con configuración segura, modo diagnóstico y control de quién puede activarlo.
**FRs cubiertos:** FR1, FR2, FR3, FR4, FR5, FR9, FR10, FR26 · **Prioridad:** P0

### Épica 2: Código y tests en un Sandbox seguro
El Programador (Claude Code) implementa la Spec con tests en un Sandbox aislado, sin internet y sin acceso a las keys reales.
**FRs cubiertos:** FR11, FR12, FR27 · **Prioridad:** P0

### Épica 3: Revisión independiente y PR
Tres Revisores independientes auditan el cambio, se hacen Rondas de corrección y se crea un PR con traza completa, clasificación de riesgo y aviso en la issue.
**FRs cubiertos:** FR14, FR15, FR16, FR17, FR18, FR19 · **Prioridad:** P0 (FR18 y FR19 en P1)

### Épica 4: Panel en vivo
El usuario ve la cadena de montaje con el nodo activo, la terminal en directo y el historial, y puede cancelar una Ejecución. Se diseña con Sally (UX) antes de implementarla.
**FRs cubiertos:** FR20, FR21, FR22, FR23 · **Prioridad:** P0 (FR22 y FR23 en P1)

### Épica 5: Control de coste y fiabilidad
Cada Ejecución tiene un presupuesto máximo que la detiene sola, y CodeNetix se comporta correctamente al reiniciarse. Es imprescindible antes del lanzamiento público.
**FRs cubiertos:** FR13, FR24 · **Prioridad:** P1 / P2

### Épica 6: GitHub Action y lanzamiento público (preview)
Cualquiera puede usar CodeNetix sin servidor y descargar imágenes firmadas. Incluye CI, versionado semver, cosign + SBOM, `SECURITY.md` y un README con los riesgos documentados.
**FRs cubiertos:** FR25 + requisitos de publicación · **Prioridad:** P2 (adelantada por estrategia)

### Épica 7: Entrada automática: sondeo, webhooks y Sentry
Las issues etiquetadas y los errores de Sentry llegan solos, sin lanzar nada a mano.
**FRs cubiertos:** FR6, FR7, FR8 · **Prioridad:** P1 (después de la preview)

## Épica 1: Instalar y obtener una Spec

El usuario arranca CodeNetix con un único comando, lanza `codenetix fix <issue>` y obtiene una Spec técnica de su issue, con configuración segura, modo diagnóstico y control de quién puede activarlo.

### Historia 1.1: Arrancar CodeNetix con un comando

Como desarrollador .NET que prueba CodeNetix,
quiero arrancarlo con un único `docker run` y comprobar que está vivo,
para empezar a usarlo sin instalar nada más ni crear cuentas.

**Criterios de aceptación:**

**Dado** un repositorio con la solución .NET 10 de AD-1 (Contracts, Host, Ingestion, Pipeline, Agents, Sandbox, GitHub, Persistence, ArchitectureTests) y `panel/` (React 19 + Vite 8)
**Cuando** se ejecuta el build
**Entonces** `ArchitectureTests` falla si algún módulo de negocio referencia a otro módulo de negocio
**Y** la imagen `codenetix` (base `aspnet:10.0`) se construye con el Panel compilado en `wwwroot`

**Dado** Docker instalado
**Cuando** el usuario ejecuta `docker run -p 127.0.0.1:5000:5000 -v codenetix-data:/data codenetix serve`
**Entonces** `GET /api/health` responde en menos de 60 s desde el arranque del contenedor (FR1)
**Y** se crea `/data/codenetix.db` (SQLite en WAL) sin intervención del usuario
**Y** `http://localhost:5000` sirve una página del Panel con un mensaje de bienvenida
**Y** no existe registro, login ni creación de cuenta

### Historia 1.2: Configurar la instancia y modo diagnóstico

Como operador de CodeNetix,
quiero configurar mis keys y tokens por variables de entorno y ver claramente si falta algo,
para no exponer secretos y saber por qué no se procesa nada.

**Criterios de aceptación:**

**Dado** que el contenedor arranca sin una variable obligatoria (key de Proveedor o token de GitHub)
**Cuando** termina el `StartupReconciler` (AD-18)
**Entonces** `IInstanceHealth` vale `Diagnostic` con la lista de lo que falta
**Y** `/api/health` y la página del Panel muestran qué falta
**Y** ningún `BackgroundService` procesa Eventos (FR2)

**Dado** una configuración completa (`CODENETIX__*` + `/config/codenetix.instance.yml` opcional, AD-10)
**Cuando** cualquier log o respuesta de la API incluye un secreto cargado
**Entonces** `ISecretRedactor` lo enmascara (`sk-…abcd`) (AD-13)
**Y** ningún secreto se escribe en SQLite

### Historia 1.3: Acceso seguro al Panel y la API

Como operador,
quiero que solo yo pueda usar el Panel y la API,
para que nadie de mi red pueda lanzar Ejecuciones con mis keys.

**Criterios de aceptación:**

**Dado** un primer arranque
**Cuando** el host se inicia
**Entonces** se genera `/data/admin.token` con permisos 0600 (AD-17)
**Y** `/api/**` y `/hubs/**` en `:5000` exigen `Authorization: Bearer <admin-token>`, y sin él responden 401

**Dado** `CODENETIX__Host__Exposed=true` sin `AccessToken` configurado
**Cuando** el host arranca
**Entonces** el arranque se aborta con un mensaje explicativo (FR4)

**Dado** el host en marcha
**Cuando** llega una petición a `:5001/webhooks/*`
**Entonces** esa ruta no exige el token de acceso (la validación de firma llega en la Épica 7)

### Historia 1.4: Lanzar trabajo con `codenetix fix`

Como desarrollador,
quiero ejecutar `codenetix fix <url-de-issue>` y que mi petición quede registrada,
para que CodeNetix empiece a trabajar sobre esa issue.

**Criterios de aceptación:**

**Dado** el contenedor en marcha
**Cuando** el usuario ejecuta `docker exec codenetix codenetix fix https://github.com/o/r/issues/12`
**Entonces** el cliente lee la URL de la API y el `admin-token` del propio contenedor y llama a `POST /api/events/manual` (AD-12)
**Y** Ingestion persiste un `EventEnvelope{Source=Manual}` en `ing_events` con la clave `manual:<url>:<requestId>` (AD-4)
**Y** el comando muestra el id del Evento en menos de 2 s (FR5)

**Dado** una URL que no es una issue de GitHub ni una incidencia de Sentry
**Cuando** se ejecuta `codenetix fix`
**Entonces** el comando devuelve un error explicativo y no se persiste ningún Evento

**Dado** una URL de una incidencia de Sentry antes de la Épica 7
**Cuando** se ejecuta `codenetix fix`
**Entonces** el comando responde "soporte de Sentry disponible próximamente" y no se persiste ningún Evento

**Dado** el mismo `requestId` enviado dos veces (reintento de red)
**Cuando** Ingestion lo recibe
**Entonces** solo existe un Evento

### Historia 1.5: Autorización del disparador

Como dueño de un repositorio,
quiero que solo quien tiene permiso de escritura pueda activar CodeNetix,
para que nadie inyecte instrucciones maliciosas a través de una issue.

**Criterios de aceptación:**

**Dado** un Evento cuyo actor no tiene permiso de escritura en el Repositorio objetivo (consultado en la API de GitHub)
**Cuando** Ingestion lo procesa
**Entonces** el Evento queda como `ignored` con el motivo "no autorizado" y no se crea ninguna Ejecución (FR26)

**Dado** un Evento manual
**Cuando** se comprueba la autorización
**Entonces** el actor es la identidad del token de GitHub de la instancia y se verifica contra ella

**Dado** cualquier Evento aceptado
**Cuando** su texto se pase a un agente en historias posteriores
**Entonces** Contracts ofrece un tipo `UntrustedText` que solo se puede renderizar dentro de un bloque delimitado como datos (AD-5)

### Historia 1.6: Ejecución con máquina de estados visible

Como desarrollador,
quiero que cada Evento aceptado se convierta en una Ejecución cuyo estado puedo consultar,
para saber en qué punto está mi petición.

**Criterios de aceptación:**

**Dado** un Evento aceptado
**Cuando** el Dispatcher lo recoge
**Entonces** crea un `PipelineRun` en `Recibida` (como mucho una Ejecución no terminal por objeto) (AD-4)
**Y** todas las transiciones pasan por los métodos del agregado, y una transición ilegal lanza una excepción (AD-2)
**Y** cada transición se persiste con su `RunEvent` en la misma transacción mediante `IRunEventSink`, con un `seq` monotónico (AD-3)

**Dado** una Ejecución
**Cuando** el usuario consulta `GET /api/runs/<runId>` o ejecuta `codenetix fix --follow`
**Entonces** ve el Estado y los `RunEvent` en orden de `seq`
**Y** existen tests de tabla para todas las transiciones válidas e inválidas del diagrama de AD-2

### Historia 1.7: Clonar y leer la configuración del repositorio

Como desarrollador,
quiero que CodeNetix respete el `codenetix.yml` de mi repositorio,
para controlar las Rutas protegidas, los Revisores y los límites.

**Criterios de aceptación:**

**Dado** una Ejecución en `Recibida`
**Cuando** el orquestador inicia el paso 1 de AD-16
**Entonces** el módulo GitHub clona el repositorio con `http.extraHeader` efímero, fija el `BaseSha` y verifica que no queda ninguna credencial en el árbol de trabajo (AD-8)
**Y** lee `codenetix.yml` del `BaseSha` y lo valida contra `schemas/codenetix.v1.json` (AD-10)
**Y** Pipeline persiste la `EffectiveConfig` como instantánea en la Ejecución

**Dado** que el repositorio no tiene `codenetix.yml`
**Cuando** se calcula la `EffectiveConfig`
**Entonces** se aplican los valores por defecto documentados (FR3)

**Dado** un `codenetix.yml` inválido
**Cuando** se valida
**Entonces** la Ejecución pasa de `Recibida` a `Fallida(InvalidRepoConfig)` con el error visible, sin llamar a ningún agente

**Dado** un `codenetix.yml` que intenta ampliar un límite de la instancia
**Cuando** se calcula la `EffectiveConfig`
**Entonces** prevalece el valor más restrictivo

### Historia 1.8: El Analista genera la Spec

Como desarrollador,
quiero que el Analista convierta mi issue en una Spec técnica precisa,
para tener claro qué se va a cambiar antes de tocar código.

**Criterios de aceptación:**

**Dado** una Ejecución con su `EffectiveConfig`
**Cuando** el orquestador entra en `Analizando`
**Entonces** el Analista (`IReasoningAgent` sobre `IChatClient`, AD-5) genera una `Spec` con problema, causa probable, cambio propuesto, criterios de aceptación y archivos afectados (FR9)
**Y** el texto de la issue se pasa como `UntrustedText` delimitado
**Y** la Spec se persiste en `run_specs` y es visible con `GET /api/runs/<runId>`

**Dado** que el modelo devuelve una salida que no cumple el esquema
**Cuando** el decorador de reintentos la valida
**Entonces** reintenta como máximo 2 veces y, si sigue fallando, la Ejecución pasa a `Fallida(SchemaInvalid)`

**Dado** un Proveedor compatible con OpenAI configurado (incluido un modelo local)
**Cuando** el modelo no soporta salida estructurada
**Entonces** se pide el JSON en el prompt y se valida igualmente contra el esquema

**Dado** que el Programador todavía no existe (Épica 2)
**Cuando** la Spec es válida
**Entonces** la Ejecución termina provisionalmente en `Fallida(StageError)` con el motivo "Programación no disponible", y la historia 2.x sustituye este paso

### Historia 1.9: Rechazo temprano con aviso en la issue

Como desarrollador,
quiero que CodeNetix me diga en la issue cuándo algo no es automatizable,
para no esperar un PR que nunca llegará.

**Criterios de aceptación:**

**Dado** que el Analista marca el Evento como "no automatizable" (falta información, cambio demasiado grande o fuera de .NET)
**Cuando** el orquestador aplica el resultado
**Entonces** la Ejecución pasa a `Fallida(NotAutomatable)` sin programar nada (FR10)
**Y** el módulo GitHub publica un comentario en la issue con el motivo, idempotente por `(runId, kind)` en `gh_notifications` (AD-8)

**Dado** que el mismo comentario se intenta publicar dos veces
**Cuando** GitHub comprueba `gh_notifications`
**Entonces** solo existe un comentario

## Épica 2: Código y tests en un Sandbox seguro

El Programador (Claude Code) implementa la Spec con tests en un Sandbox aislado, sin internet y sin acceso a las keys reales.

### Historia 2.1: Sandbox efímero por Ejecución

Como desarrollador,
quiero que el código de mi repositorio solo se ejecute dentro de un contenedor desechable,
para que nada de lo que haga pueda tocar mi máquina.

**Criterios de aceptación:**

**Dado** la imagen `codenetix-sandbox` (base `sdk:10.0` + Node, sin el CLI de Claude Code, que se instala la primera vez en el volumen `codenetix-tools` según AD-6)
**Cuando** el orquestador llama a `ISandboxManager.CreateForRun(runId)` tras el clonado (AD-6, AD-16)
**Entonces** se crea un volumen con nombre `codenetix-ws-<runId>` con el árbol de trabajo **sin `.git`**, montado en `/workspace`
**Y** el contenedor lleva la etiqueta `codenetix.run=<runId>` y límites de CPU, memoria y tiempo
**Y** funciona igual en Linux y en Docker Desktop (Windows/macOS), porque no hay ningún *bind mount* de rutas del contenedor host (NFR4)

**Dado** que la Ejecución termina en cualquier Estado
**Cuando** se ejecuta el `finally` de la Ejecución
**Entonces** el contenedor y el volumen se destruyen (FR11)

**Dado** que se cancela el token de la Ejecución
**Cuando** la sesión lo observa
**Entonces** el contenedor se detiene en 10 s como máximo

### Historia 2.2: Red aislada y Egress Gateway

Como desarrollador,
quiero que el Sandbox no tenga internet y nunca vea mis keys reales,
para que una issue maliciosa o un test hostil no pueda robarlas ni conectarse a donde quiera.

**Criterios de aceptación:**

**Dado** el host en marcha
**Cuando** se crea un Sandbox
**Entonces** el Sandbox solo está en la red `internal` `codenetix-sandbox`, a la que el contenedor `codenetix` está unido con el alias `gateway` (AD-7)

**Dado** una petición del Sandbox a `http://gateway:8080/llm` con `ANTHROPIC_API_KEY=cnx-run-<runId>-<nonce>`
**Cuando** el Gateway (YARP) la reenvía al Proveedor
**Entonces** sustituye la key ficticia por la real y retransmite el SSE sin buffer
**Y** registra el `usage` de la respuesta asociado al `runId`

**Dado** una conexión `CONNECT` a `gateway:8081`
**Cuando** el destino es un feed de NuGet configurado
**Entonces** se permite sin inspeccionar el contenido
**Y** cualquier otro destino se rechaza y se registra como `RunEvent` (FR27)

### Historia 2.3: El Programador implementa la Spec

Como desarrollador,
quiero que Claude Code implemente la Spec en una rama propia,
para obtener el cambio sin escribirlo yo.

**Criterios de aceptación:**

**Dado** una Ejecución con Spec y un Sandbox activo
**Cuando** el orquestador entra en `Programando`
**Entonces** `ClaudeCodeAgent` (`ICodingAgent`) ejecuta dentro del Sandbox `claude --bare -p <prompt> --output-format json --permission-mode acceptEdits --allowedTools "Bash,Read,Edit,Write" --permission-prompts none` (AD-5)
**Y** la Spec y el texto de la issue van como datos delimitados
**Y** la salida del proceso se emite como `RunEvent` `OutputAppended` a través de `IRunEventSink`
**Y** el "puente" provisional de la historia 1.8 desaparece: tras la Spec, la Ejecución pasa a `Programando`

**Dado** que el CLI termina con error o sin cambios en el árbol de trabajo
**Cuando** el orquestador aplica el resultado
**Entonces** cuenta como un intento fallido en `PipelineRun` (AD-2)

### Historia 2.4: Tests obligatorios con reintentos

Como desarrollador,
quiero que cada cambio compile, pase los tests e incluya al menos un test nuevo,
para no recibir código que rompe mi proyecto.

**Criterios de aceptación:**

**Dado** que el Programador ha terminado un intento
**Cuando** el orquestador llama a `ITestRunner.RunAsync(session)` (AD-6, AD-16)
**Entonces** se ejecutan `dotnet build` y `dotnet test` en el Sandbox y se produce un `TestReport` (compilación, tests ejecutados, fallidos y nuevos, comparados con una ejecución base hecha al crear el Sandbox)
**Y** el resultado lo decide el orquestador a partir del `TestReport`, no de lo que diga el agente

**Dado** un `TestReport` con fallos o sin ningún test nuevo
**Cuando** quedan intentos (3 por defecto)
**Entonces** el Programador recibe el fallo como feedback y vuelve a intentarlo
**Y** al agotarlos, la Ejecución pasa a `Fallida(TestsFailed)` (FR12)

**Dado** un `TestReport` correcto
**Cuando** el orquestador lo aplica
**Entonces** la Ejecución queda lista para la revisión; hasta la Épica 3 termina provisionalmente en `Fallida(StageError)` con el motivo "Revisión no disponible" y el `TestReport` visible por la API

### Historia 2.5: Prueba de fuego de seguridad

Como mantenedor de un proyecto open source,
quiero tests automáticos que demuestren el aislamiento,
para poder afirmar en el README que las keys están protegidas.

**Criterios de aceptación:**

**Dado** un repositorio de prueba cuyo test intenta leer `ANTHROPIC_API_KEY`, `.git/config` y el token de GitHub, y conectarse a un dominio externo
**Cuando** se ejecuta dentro del Sandbox en un test de integración con Docker
**Entonces** solo obtiene la key ficticia, no encuentra ninguna credencial y la conexión externa falla con un `RunEvent` de bloqueo (FR27, NFR1)
**Y** el test forma parte de la suite que corre en CI

## Épica 3: Revisión independiente y PR

Tres Revisores independientes auditan el cambio, se hacen Rondas de corrección y se crea un PR con traza completa, clasificación de riesgo y aviso en la issue.

### Historia 3.1: Revisores independientes emiten Veredictos

Como tech lead,
quiero que tres Revisores independientes (seguridad, calidad y tests) evalúen cada cambio,
para detectar problemas antes de que lleguen a mi PR.

**Criterios de aceptación:**

**Dado** un intento con el `TestReport` correcto
**Cuando** el orquestador calcula el diff con `IGitWorkspace.ComputeDiff(runId)` contra el `BaseSha` (AD-8) y entra en `Revisando`
**Entonces** los Revisores activos de la `EffectiveConfig` se ejecutan en paralelo, cada uno con una Etapa `Review` propia por Ronda (AD-11)
**Y** cada Revisor recibe solo la `Spec`, el `Diff` y el `TestReport`, nunca la salida del Programador (FR14, AD-5)
**Y** cada uno puede usar un Proveedor o modelo distinto, incluidos los locales compatibles con OpenAI

**Dado** la respuesta de un Revisor
**Cuando** se valida contra el esquema `Verdict`
**Entonces** se persiste en `run_verdicts` con `Approved` / `ChangesRequested` / `Rejected` y hallazgos con archivo y línea (FR15)
**Y** una respuesta inválida tras los reintentos cuenta como `ChangesRequested`

### Historia 3.2: Rondas de revisión

Como tech lead,
quiero que el Programador corrija lo que piden los Revisores,
para recibir cambios ya pulidos.

**Criterios de aceptación:**

**Dado** al menos un Veredicto `ChangesRequested` y Rondas disponibles (2 por defecto)
**Cuando** el orquestador aplica los Veredictos
**Entonces** la Ejecución vuelve a `Programando` con los hallazgos como feedback, y se repiten los tests y la revisión (FR16)

**Dado** un Veredicto `Rejected`
**Cuando** se aplica
**Entonces** la Ejecución pasa a `Fallida(ReviewRejected)` con los Veredictos visibles

**Dado** que se agotan las Rondas sin unanimidad
**Cuando** se aplica la última revisión
**Entonces** la Ejecución pasa a `Fallida(ReviewRoundsExhausted)`

### Historia 3.3: PR con traza completa

Como desarrollador,
quiero recibir un PR con toda la información de lo que se hizo y por qué,
para revisarlo y fusionarlo con confianza.

**Criterios de aceptación:**

**Dado** una aprobación unánime
**Cuando** el orquestador entra en `Decidiendo`
**Entonces** el módulo GitHub hace **un único commit** sobre el `BaseSha`, push de la rama `codenetix/<runId>` y abre un PR con la etiqueta `codenetix` (AD-8)
**Y** el cuerpo incluye el enlace al Evento, la Spec, el resumen de los Veredictos con sus hallazgos, el `TestReport` y el coste (cuando exista) (FR17)
**Y** la Ejecución pasa a `PullRequestCreated` y el PR se registra en `gh_pull_requests`

**Dado** cualquier intento de push a la rama por defecto o de merge
**Cuando** pasa por el guardián
**Entonces** se rechaza y queda registrado

### Historia 3.4: Clasificación de riesgo determinista

Como tech lead,
quiero que el PR me avise cuando toca zonas sensibles,
para revisar con más cuidado lo que importa.

**Criterios de aceptación:**

**Dado** un `PolicyInput` (rutas cambiadas, líneas del diff, Rutas protegidas, Veredictos, Ronda)
**Cuando** se llama a `MergePolicy.Classify`
**Entonces** devuelve `Minor` o `NeedsAttention` sin E/S, reloj, aleatoriedad ni LLM (AD-9, NFR6)
**Y** existen tests de tabla que cubren las Rutas protegidas, el umbral del diff y la unanimidad en la primera Ronda

**Dado** un cambio `NeedsAttention`
**Cuando** se crea el PR
**Entonces** el título y el cuerpo lo indican junto con las rutas implicadas (FR18)
**Y** en la v1 el destino es siempre un PR, sea cual sea la clase

### Historia 3.5: Aviso en la issue al terminar

Como desarrollador,
quiero ver el resultado en la issue de origen,
para enterarme sin abrir CodeNetix.

**Criterios de aceptación:**

**Dado** una Ejecución que llega a `PullRequestCreated`, `Fallida` o `Cancelada` con origen en GitHub
**Cuando** el orquestador ejecuta el paso de notificación (AD-16)
**Entonces** se publica un único comentario con el enlace al PR o el motivo (`FailureReason`) (FR19)
**Y** es idempotente por `(runId, kind)` en `gh_notifications`

## Épica 4: Panel en vivo

El usuario ve la cadena de montaje con el nodo activo, la terminal en directo y el historial, y puede cancelar. **Antes de implementar esta épica, Sally define el diseño visual (`bmad-ux`).**

### Historia 4.1: Cadena de montaje en vivo

Como desarrollador,
quiero ver en el Panel cómo avanza mi Ejecución, nodo a nodo,
para entender qué está haciendo cada agente en cada momento.

**Criterios de aceptación:**

**Dado** el Panel abierto por primera vez
**Cuando** pide el `admin-token`
**Entonces** el usuario lo introduce una vez y queda guardado en el navegador (AD-17)

**Dado** una Ejecución activa
**Cuando** el Panel se suscribe al hub `/hubs/runs`
**Entonces** muestra un grafo (React Flow) con las Etapas y el nodo activo resaltado, incluida una Etapa `Review` por Revisor
**Y** cada cambio de Estado se refleja en menos de 1 s sin recargar la página (FR20)
**Y** al reconectar, reanuda con `GET /api/runs/<runId>/events?afterSeq=N` sin perder eventos (AD-3)
**Y** los tipos de los mensajes se generan desde el OpenAPI (unión discriminada de `RunEvent`) y la prueba de contrato valida cada tipo (AD-14)

### Historia 4.2: Terminal en vivo

Como desarrollador,
quiero ver la salida del agente y de los tests en directo,
para seguir el trabajo como si estuviera en mi terminal.

**Criterios de aceptación:**

**Dado** una Etapa activa que emite `OutputAppended`
**Cuando** el usuario selecciona el nodo
**Entonces** ve la salida en una terminal (xterm.js), en lotes de 250 ms como máximo (FR21)
**Y** los secretos aparecen enmascarados (AD-13)
**Y** al superar 5 MB por Etapa se muestra el aviso `OutputTruncated`

### Historia 4.3: Historial de Ejecuciones

Como desarrollador,
quiero consultar las Ejecuciones pasadas,
para revisar resultados, costes y Veredictos.

**Criterios de aceptación:**

**Dado** Ejecuciones terminadas
**Cuando** el usuario abre el historial
**Entonces** ve el Estado, `FailureReason`, la Spec, los Veredictos, el enlace al PR, la duración y el coste (cuando exista) (FR22)
**Y** el historial se conserva tras reiniciar el contenedor si el volumen `/data` persiste

### Historia 4.4: Cancelar una Ejecución

Como desarrollador,
quiero cancelar una Ejecución que va mal,
para no gastar tokens ni recibir un PR que no quiero.

**Criterios de aceptación:**

**Dado** una Ejecución en `Recibida`, `Analizando`, `Programando` o `Revisando`
**Cuando** el usuario pulsa "Cancelar"
**Entonces** `IRunControl.RequestCancel` registra la petición y el orquestador aplica `Cancelada` (AD-2)
**Y** el Sandbox se detiene en 10 s como máximo y no se crea ningún PR (FR23)

**Dado** una Ejecución en `Decidiendo` o en un estado terminal
**Cuando** se pide la cancelación
**Entonces** la API responde 409 y el botón aparece deshabilitado

### Historia 4.5: Lanzar trabajo desde el Panel

Como desarrollador,
quiero pegar la URL de una issue en el Panel,
para no tener que usar la terminal.

**Criterios de aceptación:**

**Dado** el Panel autenticado
**Cuando** el usuario pega una URL válida y confirma
**Entonces** el Panel llama a `POST /api/events/manual` con un `requestId` nuevo (AD-4) y navega a la nueva Ejecución
**Y** las URLs no válidas muestran el mismo error que el CLI

## Épica 5: Control de coste y fiabilidad

Cada Ejecución tiene un presupuesto máximo que la detiene sola, y CodeNetix se comporta correctamente al reiniciarse. Imprescindible antes del lanzamiento público.

### Historia 5.1: Libro de presupuesto y coste visible

Como desarrollador que paga sus propios tokens,
quiero ver cuánto cuesta cada Ejecución,
para controlar mi gasto.

**Criterios de aceptación:**

**Dado** la tabla de precios por modelo en la Configuración de instancia (con valores por defecto documentados)
**Cuando** el Gateway (Programador) o el middleware obligatorio de `IChatClient` (Analista y Revisores) registran un consumo
**Entonces** `IBudgetLedger.Charge` lo anota en `run_budget` y emite `TokensConsumed` (AD-15)
**Y** el coste aparece en la API, en el Panel y en el cuerpo del PR
**Y** un modelo sin precio conocido se muestra como "no estimable" (incluidos los locales)

### Historia 5.2: Corte automático por presupuesto

Como desarrollador,
quiero que una Ejecución se pare sola al llegar al presupuesto,
para no llevarme sustos en la factura.

**Criterios de aceptación:**

**Dado** `budget_usd` en la `EffectiveConfig`
**Cuando** un cargo devuelve `Exhausted`
**Entonces** el Gateway responde `402` con `codenetix:budget_exhausted`, el adaptador lo traduce a `StageOutcome.BudgetExhausted`, y el ledger solicita la cancelación cooperativa
**Y** la Ejecución pasa a `Fallida(BudgetExceeded)` (FR13)
**Y** el CLI de Claude Code se lanza además con `--max-budget-usd <restante>` como segunda barrera

### Historia 5.3: Comportamiento tras un reinicio

Como operador,
quiero que un reinicio no deje Ejecuciones colgadas ni contenedores huérfanos,
para confiar en que CodeNetix se recupera solo.

**Criterios de aceptación:**

**Dado** Ejecuciones no terminales y Sandboxes activos
**Cuando** el host se reinicia
**Entonces** `StartupReconciler` aplica el orden de AD-18: migraciones → salud → Ejecuciones no terminales a `Fallida(HostRestarted)` → limpieza de contenedores `codenetix.run` y volúmenes `codenetix-ws-*` → arranque del Dispatcher (FR24)
**Y** los Eventos aceptados que aún no tenían Ejecución se procesan después del arranque

## Épica 6: GitHub Action y lanzamiento público (preview)

Cualquiera puede usar CodeNetix sin servidor y descargar imágenes firmadas. Incluye CI, versionado, `SECURITY.md` y un README con los riesgos documentados.

### Historia 6.1: Modo `codenetix action`

Como mantenedor de una librería .NET sin servidor,
quiero ejecutar la factoría completa dentro de GitHub Actions,
para usarla sin desplegar nada.

**Criterios de aceptación:**

**Dado** un runner `ubuntu-*` con Docker
**Cuando** se ejecuta `codenetix action`
**Entonces** construye un `EventEnvelope{Source=GitHubAction}` desde `GITHUB_EVENT_PATH` y pasa por Ingestion, incluida la autorización de FR26 (AD-12)
**Y** usa el mismo Pipeline, Sandbox y Gateway, con SQLite temporal
**Y** vuelca los `RunEvent` a la salida estándar del job

### Historia 6.2: Acción `composite` publicada

Como usuario,
quiero añadir CodeNetix a mi repositorio con unas pocas líneas de YAML,
para probarlo en minutos.

**Criterios de aceptación:**

**Dado** `action.yml` como acción `composite`
**Cuando** se dispara al etiquetar una issue
**Entonces** lanza `ghcr.io/n3rvs/codenetix:<tag> action` con el socket de Docker del runner (FR25)
**Y** lee la key del Proveedor de los *secrets* del repositorio
**Y** la documentación incluye un workflow de ejemplo, los permisos necesarios de `GITHUB_TOKEN`, el aviso de que los *secrets* no llegan a forks y el aviso de que los PR creados con `GITHUB_TOKEN` no disparan workflows

### Historia 6.3: CI del proyecto

Como mantenedor,
quiero que cada cambio en CodeNetix se valide automáticamente,
para publicar versiones fiables.

**Criterios de aceptación:**

**Dado** un push o un PR al repositorio de CodeNetix
**Cuando** se ejecuta el workflow de CI
**Entonces** se ejecutan el build, `ArchitectureTests`, los tests unitarios, los tests de integración con Docker (incluida la prueba de fuego 2.5) y el build `buildx` para x64 y arm64 de ambas imágenes

### Historia 6.4: Publicación firmada de imágenes

Como usuario preocupado por la seguridad,
quiero descargar imágenes firmadas y con SBOM,
para verificar que no han sido manipuladas.

**Criterios de aceptación:**

**Dado** una etiqueta semver `vX.Y.Z`
**Cuando** se ejecuta el workflow de release
**Entonces** se publican `codenetix` y `codenetix-sandbox` en GHCR con las etiquetas `vX.Y.Z` y `vX.Y` (sin `latest` en la documentación)
**Y** se firman con cosign y llevan SBOM
**Y** Dependabot (o Renovate) vigila las imágenes base y el CLI de Claude Code
**Nota:** las imágenes se publican en `ghcr.io/n3rvs/` (OQ-1 resuelta: cuenta de GitHub `N3rvs`)

### Historia 6.5: Documentación de lanzamiento

Como nuevo usuario,
quiero un README que me lleve al primer PR en menos de 10 minutos,
para probar CodeNetix sin fricción.

**Criterios de aceptación:**

**Dado** el repositorio público
**Cuando** un usuario nuevo sigue el README
**Entonces** encuentra un quickstart con `docker run -p 127.0.0.1:5000:5000 …` y `codenetix fix` (SM-1)
**Y** el README documenta el riesgo del socket de Docker, el modelo de seguridad del Sandbox y el uso de keys propias
**Y** existen `SECURITY.md` (política de divulgación), los créditos a la inspiración (Eric Risco) y la licencia elegida
**Y** el repositorio incluye `LICENSE` (Apache-2.0) y la guía de contribución exige DCO (`git commit -s`) (OQ-2 resuelta)

## Épica 7: Entrada automática: sondeo, webhooks y Sentry

Las issues etiquetadas y los errores de Sentry llegan solos, sin lanzar nada a mano.

### Historia 7.1: Sondeo de issues etiquetadas

Como desarrollador sin servidor público,
quiero que CodeNetix recoja solo las issues con la etiqueta `codenetix`,
para automatizar sin configurar webhooks.

**Criterios de aceptación:**

**Dado** repositorios configurados para el sondeo
**Cuando** pasa el intervalo (5 minutos por defecto)
**Entonces** Ingestion consulta el timeline `labeled` de las issues abiertas con la etiqueta y crea Eventos con la clave `gh:<owner>/<repo>#<issue>:labeled:<labelEventId>` (FR6, AD-4)
**Y** una issue con una Ejecución no terminal no genera otra; reetiquetarla sí genera un Evento nuevo
**Y** en modo `Diagnostic` el sondeo no se ejecuta

### Historia 7.2: Webhooks de GitHub

Como tech lead con servidor,
quiero que las issues etiquetadas se procesen al instante,
para no esperar al sondeo.

**Criterios de aceptación:**

**Dado** un webhook de GitHub en `:5001/webhooks/github`
**Cuando** la firma es válida
**Entonces** responde 2xx en menos de 1 s y procesa el Evento de forma asíncrona, con la misma clave de deduplicación que el sondeo (FR7)
**Y** si la firma no es válida, responde 401 y no se procesa
**Y** en modo `Diagnostic` responde 503

### Historia 7.3: Sentry: webhooks y correspondencia con el repositorio

Como tech lead,
quiero que los errores nuevos o en regresión de Sentry generen Ejecuciones en el repositorio correcto,
para que los fallos de producción se arreglen solos.

**Criterios de aceptación:**

**Dado** el mapa de Sentry a repositorio en la Configuración de instancia (AD-10)
**Cuando** llega un webhook de Sentry con firma válida
**Entonces** se crea un Evento con la clave `sentry:<issueId>`, o `sentry:<issueId>:regression:<n>` si es una regresión (FR7, FR8)
**Y** N eventos del mismo error generan como mucho una Ejecución no terminal
**Y** un proyecto sin correspondencia queda como `ignored` con su motivo visible en el Panel

### Historia 7.4: Contexto de Sentry para el Analista

Como desarrollador,
quiero que el Analista use la traza completa del error,
para que la Spec apunte a la causa real.

**Criterios de aceptación:**

**Dado** `Sentry:AuthToken` (y `Sentry:BaseUrl` opcional) configurados
**Cuando** se analiza un Evento de Sentry
**Entonces** el Analista recibe la traza, el contexto y la frecuencia del error como `UntrustedText`
**Y** `codenetix fix <url-de-sentry>` deja de responder "próximamente" y crea el Evento (FR5)
