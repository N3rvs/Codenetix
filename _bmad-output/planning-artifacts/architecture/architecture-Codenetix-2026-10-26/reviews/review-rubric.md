---
reviewer: rubric-walker (architecture spine)
target: ARCHITECTURE-SPINE.md (CodeNetix v1, status draft, 2026-10-27)
prd: prds/prd-Codenetix-2026-10-26/prd.md
date: 2026-10-27
---

# Revisión por rúbrica — Architecture Spine CodeNetix v1

## Veredicto

El spine es sólido en su estructura: paradigma claro, dependencias forzadas por una prueba de arquitectura, una sola máquina de estados, un solo log de eventos, un guardián de Git y una política pura. Sin embargo, **no está listo para trocearse en épicas**. Tiene un defecto de despliegue que rompe el modo servidor tal y como está escrito (montaje del workspace con Docker-out-of-Docker). Además, el mecanismo del Egress Gateway no está especificado hasta el punto en que la regla sea implementable. Hay huecos en la máquina de estados que contradicen FR-3 y FR-24, y faltan decisiones sobre varios puntos en los que dos historias divergirían: el coste o presupuesto entre módulos, la exposición de red frente a los webhooks (FR-4) y las credenciales en el clon. Tampoco hay una sección de *Open Questions*, y la envolvente operativa del propio proyecto (release, CI y firma de imágenes) está sin decidir.

Recuento: **1 crítico, 6 altos, 9 medios, 6 bajos**.

---

## Críticos

### C-1. El workspace clonado por el host no puede montarse "como volumen" en el Sandbox hermano (Docker-out-of-Docker)
- **Ubicación:** AD-6 ("El repositorio se monta como volumen `/workspace`"), AD-8 ("clona al directorio de trabajo que luego se monta") y Despliegue (`codenetix:serve + volumen /data + socket Docker`).
- **Problema:** en modo servidor, CodeNetix corre en un contenedor y crea los Sandboxes a través del socket del **host**. Una ruta de *bind mount* que se pasa a Docker se resuelve en el sistema de archivos del host, no en el del contenedor `codenetix`. Si el módulo GitHub clona en `/data/work/<runId>` dentro de su contenedor y el módulo Sandbox pide montar esa ruta, Docker montará un directorio vacío o inexistente del host. Esto ocurre en Linux y aún más en Docker Desktop (Windows/macOS, NFR-4). Los equipos de GitHub y de Sandbox implementarán cosas distintas, o ninguna funcionará. En el modo Action pasa lo mismo si `codenetix action` corre dentro de un contenedor.
- **Corrección sugerida:** añadir una regla explícita a AD-6/AD-8. Opción recomendada: **un volumen Docker con nombre por Ejecución** (`codenetix-ws-<runId>`, etiquetado `codenetix.run`). El host lo crea y lo puebla, ya sea montándolo también en un contenedor auxiliar de clonado o clonando mediante un contenedor *init* sin red a internet salvo GitHub. El Sandbox lo monta y se destruye en el mismo `finally`, y la limpieza de huérfanos de AD-6 lo incluye. Alternativa: exigir que `/data` sea un *bind mount* con la **misma ruta** en el host y en el contenedor (más frágil en Docker Desktop). Hay que decidir y escribir cuál de las dos se adopta, y cómo vuelven los cambios al host para el commit y el push (AD-8).

---

## Altos

### H-1. El Egress Gateway no especifica su mecanismo; tal como está escrito no es implementable de forma única
- **Ubicación:** AD-7.
- **Problema:** la regla pide tres cosas a la vez: (a) **sustituir la key ficticia por la real**, (b) **contar tokens** y (c) filtrar destinos de NuGet. Con HTTPS y un proxy `CONNECT`, el Gateway no ve ni las cabeceras ni las respuestas, así que (a) y (b) son imposibles sin MITM TLS, que a su vez exige una CA propia confiada en la imagen del Sandbox. Dos implementadores elegirán caminos distintos: MITM con CA, o *reverse proxy* con `ANTHROPIC_BASE_URL`. Además, la red `codenetix-sandbox` es `internal`, y el contenedor `codenetix` tiene que estar conectado a esa red y a la externa para servir de Gateway. Esto no se indica.
- **Corrección sugerida:** fijar dos modos en la regla. **Proveedor del Programador:** *reverse proxy* HTTP en `http://gateway:<puerto>` dentro de la red interna, con `ANTHROPIC_BASE_URL` apuntando a él. El Gateway inyecta `x-api-key` real, lee `usage` de las respuestas (incluido el streaming) y emite `RunEvent`. **NuGet:** proxy `CONNECT` con *allowlist* por host (SNI o host del `CONNECT`), sin inspección. **Topología:** el contenedor `codenetix` se adjunta a `codenetix-sandbox` con el alias `gateway`. Hay que añadir una prueba de integración que verifique que una conexión a un host no listado falla y genera un `RunEvent`.

### H-2. La máquina de estados contradice FR-3 y FR-24 (faltan transiciones)
- **Ubicación:** AD-2, diagrama de estados.
- **Problema:**
  - FR-3: un `codenetix.yml` inválido termina en `Fallida` "sin llegar a llamar a ningún agente". Hace falta `Recibida → Fallida`, que no existe (salvo que se valide dentro de `Analizando`, lo que no está dicho).
  - FR-24: al reiniciar, **toda** Ejecución activa pasa a `Fallida` ("interrumpida"), también desde `Recibida` y `Decidiendo`. `Recibida → Fallida` no existe.
  - FR-23 y FR-13: no hay `Decidiendo → Cancelada`. Es defendible, pero debe ser explícito (¿se rechaza la cancelación durante la creación del PR?).
  - Con el tipo de excepción de AD-2, el código de arranque de FR-24 lanzará una excepción, o alguien añadirá un "atajo" que se salte el agregado y viole la propia AD-2.
- **Corrección sugerida:** añadir `Recibida → Fallida` y `Decidiendo → Cancelada` (o declarar que no se puede cancelar en `Decidiendo`), y una regla de que **todo estado no terminal puede pasar a `Fallida`** con un `FailureReason` tipado (`InvalidRepoConfig`, `Interrupted`, `BudgetExceeded`, `TestsFailed`, `Rejected`, `NotAutomatable`, `SchemaInvalid`…). El enum de motivos debe estar en Contracts, para que el Panel, el comentario de FR-19 y el cuerpo del PR coincidan.

### H-3. El presupuesto y el coste (FR-13, NFR-5, FR-17, FR-22) no tienen un único dueño
- **Ubicación:** AD-7 ("El consumo de tokens que pasa por el Gateway se contabiliza"), mapa de capacidades (FR-13 → Sandbox + Pipeline).
- **Problema:** solo los tokens del Programador pasan por el Gateway. El Analista y los Revisores llaman al LLM desde el host con `IChatClient`, y nadie dice dónde se cuentan. Tampoco se decide quién **detiene** la Ejecución al superar el presupuesto, ni cómo se convierten tokens en "coste estimado" (tabla de precios por modelo, ¿dónde vive?, ¿y los modelos locales a coste 0?). Además, la Etapa de Programación corre dentro de Claude Code: para cortarla a mitad hay que matar el contenedor o hacer que el Gateway rechace las peticiones. Sin una regla, Agents, Sandbox y Pipeline implementarán tres contadores.
- **Corrección sugerida:** añadir un AD "Contabilidad de coste". Cada llamada a LLM (Gateway e `IChatClient`, mediante un *middleware* de `IChatClient`) emite un `RunEvent` `TokensConsumed {stage, provider, model, in, out}`. Un único `CostLedger`/`BudgetGuard` del módulo Pipeline agrega y decide. Al superar el presupuesto, el Gateway rechaza las siguientes peticiones del Programador y el orquestador aplica `→ Fallida(BudgetExceeded)`. La tabla de precios es configurable en la Configuración de instancia, con valores por defecto documentados y precio desconocido = "no estimable".

### H-4. FR-4 (acceso y exposición) no está gobernado, y choca con los webhooks
- **Ubicación:** mapa de capacidades (FR-1 a FR-4 → AD-10..13, ninguno trata el acceso); Despliegue ("`:5000`, solo en localhost por defecto").
- **Problema:** dentro de un contenedor, la app tiene que escuchar en `0.0.0.0` para que funcione `-p`. "Solo localhost" depende del `-p 127.0.0.1:5000:5000` del usuario, y la app **no puede saber** si está expuesta. Por tanto, la regla de FR-4 ("sin token configurado no arranca expuesto") no es verificable tal cual. Además, FR-7 necesita que los webhooks lleguen desde internet, es decir, exposición, mientras que FR-4 exime a los webhooks del token. Sin una decisión, el Host implementará un middleware de autenticación ad hoc y alguien olvidará eximir (o no eximir) un endpoint.
- **Corrección sugerida:** un AD de acceso con las siguientes reglas. (1) Variable `CODENETIX__Host__Exposed` (o detección por `ASPNETCORE_URLS`) que, si vale `true`, exige `AccessToken` o aborta el arranque. (2) Middleware único que exige el token en `/api/**` y `/hubs/**` cuando `Exposed=true`; `/webhooks/**` lleva solo la verificación de firma. (3) Opcionalmente, un **listener separado** para webhooks (p. ej. `:5001`), para poder exponer solo ese puerto. (4) El README publica el `docker run` con `127.0.0.1:` explícito. También hay que decidir cómo se autentica `codenetix fix` cuando hay token (ver M-3).

### H-5. El token de GitHub puede entrar al Sandbox a través del clon
- **Ubicación:** AD-8 ("El token de GitHub nunca se pasa al Sandbox"), AD-6 (montaje del repo).
- **Problema:** si el módulo GitHub clona con `https://x-access-token:<token>@github.com/...` (lo más habitual), el token queda en `.git/config` del workspace que luego se monta en `/workspace`. El código de `dotnet test` puede leerlo, lo que viola FR-27 y NFR-1. La regla prohíbe "pasarlo", pero no impide esta fuga indirecta.
- **Corrección sugerida:** añadir a AD-8: "el clon usa la cabecera `http.extraHeader`, o un *credential helper* efímero, que no se persiste. Antes de montar, se verifica que `.git/config` no contiene credenciales y que el remoto es la URL pública". Debe haber una prueba que inspeccione el workspace montado buscando el token, reutilizando los patrones de `ISecretRedactor`.

### H-6. El modo Action contradice al PRD sin decirlo, y su forma de empaquetado no está decidida
- **Ubicación:** AD-12 (`codenetix action` usa Sandbox con el Docker del runner); PRD FR-25 ("el runner efímero de GitHub hace de Sandbox") y nota de FR-11.
- **Problema:** el spine es más estricto que el PRD (es razonable), pero no registra la desviación, y quien escriba las historias de FR-25 leerá dos cosas distintas. Tampoco se decide si `action.yml` es una acción `docker` (contenedor sin socket del runner por defecto, lo que haría imposible crear el Sandbox) o `composite` (que ejecuta `docker run` con el socket del runner). Además, en este modo FR-26 (el actor que pone la etiqueta tiene permiso de escritura) y el `GITHUB_TOKEN` frente a un PAT, necesario para que el PR dispare CI, quedan sin regla.
- **Corrección sugerida:** declarar la desviación en AD-12 ("el Sandbox también se usa en Action; se actualiza la nota del PRD"). Fijar `action.yml` como acción `composite` que lanza `ghcr.io/<org>/codenetix:<tag> action` con `-v /var/run/docker.sock` y aplica también C-1. Fijar que FR-26 se verifica igual en este modo, y documentar el token: `GITHUB_TOKEN` con permisos `contents: write, pull-requests: write, issues: write`, sabiendo que los PRs creados con `GITHUB_TOKEN` no disparan workflows.

---

## Medios

### M-1. La entidad EF y el `DbContext` único chocan con AD-1 y AD-11
- **Ubicación:** AD-1, AD-11, módulo Persistence.
- **Problema:** Persistence "solo referencia Contracts", pero tiene el `DbContext` de las tablas `ing_`, `run_` y `gh_`. Las entidades tienen que estar en Contracts (que entonces engorda y filtra el modelo interno) o en los módulos (y Persistence no puede verlas). AD-2 además exige que la transición y el `RunEvent` vayan en **una sola transacción**, lo que obliga a decidir quién abre esa transacción.
- **Corrección sugerida:** cada módulo aporta su configuración EF (`IEntityTypeConfiguration` registrada mediante una interfaz `IModelContributor` de Contracts), y Persistence compone un único `DbContext` en tiempo de ejecución. Alternativa: un `DbContext` por módulo sobre el mismo archivo, con historial de migraciones por esquema/prefijo. Hay que fijar la **unidad de trabajo** de AD-2 (transacción del orquestador del Pipeline) y completar los prefijos (`agt_`? `sbx_`? ¿dónde viven `RunEvent` y el presupuesto?).

### M-2. La clave de deduplicación por Fuente no está definida (FR-6 "salvo que se reetiquete", FR-7)
- **Ubicación:** AD-4.
- **Problema:** la regla exige una clave única pero no dice cómo se compone. FR-6 permite una nueva Ejecución si se **reetiqueta**. FR-7 deduplica por ID de entrega **y** por incidencia, y la regresión de Sentry debería poder disparar de nuevo. Sondeo y webhook pueden producir el mismo Evento con claves distintas. Si no se fija, dos historias (polling y webhook) duplicarán Ejecuciones.
- **Corrección sugerida:** una tabla en el spine. GitHub: `gh:<repo>#<issue>:label:<labeled_event_id|timestamp>`, común para sondeo y webhook (el sondeo usa el timeline `labeled`). Sentry: `sentry:<issueId>` mientras exista una Ejecución activa o terminada sin regresión, y `sentry:<issueId>:regression:<n>` tras una regresión. CLI: `cli:<url>:<nonce>`. Además, "como mucho una activa" se comprueba por clave de **objeto** (repo+issue), no de entrega.

### M-3. `codenetix fix` mediante `docker exec`: UX y autenticación sin cerrar
- **Ubicación:** AD-12.
- **Problema:** UJ-1 muestra `codenetix fix <url>` ejecutado tal cual. El spine dice "normalmente con `docker exec`", sin especificar si existe un binario o alias en el host, ni cómo se descubre la URL de la API ni cómo se pasa el token si está expuesta. Tampoco hay ninguna nota sobre FR-5 con URLs de **Sentry**, que requieren un token de API de Sentry (no figura en la Configuración de instancia).
- **Corrección sugerida:** fijar `docker exec codenetix codenetix fix <url>` como forma soportada, con el cliente leyendo `CODENETIX__Host__Url` y `AccessToken` del entorno del propio contenedor, y documentar un alias. Añadir `Sentry:AuthToken` (y la URL base para Sentry autoalojado) a la Configuración de instancia de AD-10, ya que también hace falta para obtener la traza completa en UJ-2.

### M-4. Política común de reintentos y salida estructurada sin dueño
- **Ubicación:** AD-5; FR-9 (2 reintentos de esquema), FR-12 (3 reintentos de tests), FR-15 (reintento y luego "cambios solicitados"), FR-16 (2 rondas).
- **Problema:** no se decide quién cuenta los reintentos (¿el agregado `PipelineRun` o cada agente?) ni si los reintentos de tests son nuevas invocaciones de `claude -p` o el bucle interno de Claude Code. Cada Etapa lo implementará a su manera, y los contadores no sobrevivirán ni se mostrarán de forma uniforme.
- **Corrección sugerida:** regla en AD-2/AD-5: los contadores de Ronda y de intentos de Programación viven en `PipelineRun` y se emiten como `RunEvent`. Los reintentos por esquema inválido son un *decorator* común de `IReasoningAgent` (N configurable). Un intento de Programación es una invocación de `ICodingAgent` seguida de `dotnet build/test` ejecutado **por el orquestador** en el Sandbox, no confiando en lo que reporte el agente.

### M-5. Los tipos de los mensajes de SignalR quedan fuera de los "contratos generados"
- **Ubicación:** AD-14.
- **Problema:** OpenAPI no describe los mensajes del hub. Los tipos de `RunEvent` (y sus `payload` por `type`) se escribirán a mano en el Panel, que es justo la divergencia que AD-14 quiere evitar.
- **Corrección sugerida:** exponer los esquemas de `RunEvent` en el OpenAPI (p. ej. un endpoint de historial `GET /api/runs/{id}/events` que devuelve la unión discriminada) para que la generación los incluya, o generar TS desde Contracts con una herramienta fijada. El hub solo transporta ese mismo tipo.

### M-6. Modo diagnóstico (FR-2) y ciclo de vida de los workers sin regla transversal
- **Ubicación:** Host, AD-10.
- **Problema:** FR-2 exige arrancar sin las variables obligatorias, mostrar en el Panel qué falta y **no procesar Eventos**. Esto afecta a Ingestion (sondeo, webhooks), al Dispatcher y a la limpieza de AD-6. Sin regla, cada worker decidirá si arranca.
- **Corrección sugerida:** el Host valida `InstanceConfig` y publica un `IInstanceHealth` (`Ready | Diagnostic(reasons)`). Todos los `BackgroundService` y endpoints de entrada consultan ese estado, y los webhooks responden 503 en diagnóstico. Añadir `GET /api/health` (útil también como `HEALTHCHECK` del Dockerfile).

### M-7. Retención y volumen del log de eventos
- **Ubicación:** AD-3 (líneas de terminal como `RunEvent` append-only en SQLite).
- **Problema:** la salida de Claude Code y de `dotnet test` puede llegar a decenas de MB por Ejecución. Con *append-only* y sin retención, `/data` crece sin límite, y nada impide que alguien añada un `DELETE` ad hoc.
- **Corrección sugerida:** decidir límites (p. ej. tamaño máximo de salida por Etapa y truncado con un `RunEvent` `OutputTruncated`) y una política de retención explícita (configurable, por defecto ilimitada o de N días), o diferirla de forma explícita en *Deferred* y justificar por qué no causa divergencia.

### M-8. Falta la sección de Open Questions; los huecos quedan implícitos
- **Ubicación:** documento completo.
- **Problema:** la rúbrica pide que todo lo que el spine posee esté decidido, diferido o como pregunta abierta. Hay decisiones sin ninguno de esos tres estados: C-1, el mecanismo de H-1, el tipo de token de GitHub (PAT frente a GitHub App), las credenciales de feeds NuGet privados (que tendrían que pasar por el Gateway igual que la key) y el soporte de Sentry autoalojado.
- **Corrección sugerida:** añadir `## Open Questions` con su dueño y cuándo se resuelve cada una (antes de qué épica).

### M-9. Envolvente operativa del propio proyecto (release y cadena de suministro) sin decidir
- **Ubicación:** Despliegue y entornos.
- **Problema:** es un lanzamiento open source público con imágenes multiarquitectura en GHCR que **ejecutan código no confiable con acceso al socket de Docker**. No se decide: el pipeline de CI/CD del repo (build, tests de arquitectura, tests de integración con Docker, build `buildx` x64/arm64), el esquema de versiones (semver, etiqueta `latest` o no), cómo se actualiza el CLI de Claude Code fijado en la imagen, la firma o procedencia de las imágenes (cosign/SBOM), la política de seguridad (`SECURITY.md`, divulgación) y la compatibilidad de migraciones al bajar de versión. También falta el `<org>` de GHCR.
- **Corrección sugerida:** una subsección "Release y operación del proyecto": GitHub Actions para el CI; `release-please`/tags semver; imágenes `vX.Y.Z` y `vX.Y`, sin `latest` por defecto en el README; cosign + SBOM; Renovate/Dependabot para la imagen del Sandbox; "las migraciones son solo hacia adelante y antes de actualizar se recomienda copiar `/data`". Hay que decidir el `<org>`.

---

## Bajos

### L-1. Falta la correspondencia de los Estados y valores de Veredicto con el código
- **Ubicación:** Consistency Conventions (tabla de nombres).
- **Problema:** el diagrama usa `Recibida`, `PRCreado`… y el código va en inglés. Los valores serializados (API, `RunEvent`, Panel) pueden divergir (`PrCreated` frente a `PullRequestCreated`; `aprobado` frente a `Approved`). Lo mismo pasa con `RiskClass` (`menor` / `requiere atención`) y con las Especialidades (`seguridad`, `calidad`, `tests`), que el usuario escribe en `codenetix.yml`.
- **Corrección sugerida:** añadir a la tabla los enums con su valor serializado y fijar el idioma de las claves de `codenetix.yml` (lo que escribe el usuario es un contrato público).

### L-2. La dependencia del Host hacia Contracts no aparece en el diagrama, y Panel→Host es la única arista externa
- **Ubicación:** diagrama de AD-1.
- **Corrección sugerida:** añadir `Host --> Contracts` y aclarar si `ArchitectureTests` también comprueba que el Panel solo usa los tipos generados (p. ej. con una regla de lint que prohíba `fetch` fuera del cliente generado).

### L-3. El riesgo del socket de Docker se amplifica porque el Gateway vive en el mismo proceso
- **Ubicación:** AD-6, AD-7, *Deferred* ("Endurecer el socket").
- **Problema:** el Sandbox habla directamente con un proceso que tiene el socket de Docker (es decir, root en el host). Un fallo de parsing en el Gateway equivale a escapar al host. Diferirlo es aceptable para la v1, pero el razonamiento debería constar.
- **Corrección sugerida:** documentarlo en el README y mantener el Gateway como un servidor mínimo (Kestrel/YARP con allowlist estricta y sin endpoints de administración en esa interfaz). Valorar para la v1.1 sacar el Gateway a un contenedor *sidecar* sin socket.

### L-4. FR-10 y FR-19: el comentario en la issue no está atado al cierre de la Ejecución
- **Ubicación:** AD-8, mapa (FR-17 a FR-19).
- **Corrección sugerida:** regla: "al entrar en un estado terminal, el orquestador invoca `IGitHubNotifier` una sola vez (idempotente por `runId`)". Esto cubre `Fallida`, `PRCreado` y `Cancelada`.

### L-5. Esquema público de `codenetix.yml`
- **Ubicación:** AD-10 y *Deferred* ("librería de validación").
- **Problema:** la librería puede diferirse, pero el **formato del esquema** es un contrato público que usan el editor del usuario y la documentación.
- **Corrección sugerida:** publicar un JSON Schema versionado (`schemas/codenetix.v1.json`) como fuente de verdad, del que se derivan la validación y la documentación.

### L-6. La licencia condiciona las dependencias
- **Ubicación:** *Deferred* ("Licencia… no afecta a la estructura").
- **Problema:** es casi cierto, pero el PRD la marca como bloqueante antes del primer commit público, y la elección (AGPL, FSL o Apache) puede afectar a la redistribución en la imagen del Sandbox de componentes de terceros (CLI de Claude Code, que tiene sus propios términos).
- **Corrección sugerida:** pasarla a Open Questions, con la nota "verificar que se puede redistribuir `@anthropic-ai/claude-code` dentro de una imagen pública; si no, instalarlo al primer arranque".

---

## Cobertura del PRD

| Requisito | Estado en el spine | Observación |
| --- | --- | --- |
| FR-1 | Cubierto | AD-11, AD-12 |
| FR-2 | Parcial | Falta el modo diagnóstico (M-6) |
| FR-3 | Parcial | Falta la transición `Recibida → Fallida` (H-2) |
| FR-4 | **No gobernado** | H-4 |
| FR-5 | Parcial | CLI y Sentry (M-3) |
| FR-6 | Parcial | Sondeo sin worker ni clave (M-2) |
| FR-7, FR-8 | Cubierto | AD-4, AD-10; falta la clave de deduplicación (M-2) |
| FR-9, FR-10 | Cubierto | Reintentos (M-4); comentario (L-4) |
| FR-11, FR-12 | Cubierto en intención | Roto en ejecución por C-1; reintentos (M-4) |
| FR-13 | **Parcial** | H-3 |
| FR-14 a FR-16 | Cubierto | AD-5 |
| FR-17, FR-18 | Cubierto | AD-8, AD-9 |
| FR-19 | Cubierto débilmente | L-4 |
| FR-20 a FR-22 | Cubierto | AD-3, AD-14; tipos del hub (M-5); retención (M-7) |
| FR-23 | Parcial | Cancelación en `Decidiendo` (H-2); el mecanismo de parada del contenedor no está dicho |
| FR-24 | Parcial | Transiciones (H-2); reprocesar Eventos aceptados implícito en el Dispatcher |
| FR-25 | Divergente del PRD | H-6 |
| FR-26 | Cubierto en servidor | Modo Action sin regla (H-6) |
| FR-27 | Cubierto en intención | H-1, H-5 |
| NFR-1, NFR-2 | Cubierto | Dependen de C-1, H-1, H-5 |
| NFR-3 | Cubierto | Sin telemetría |
| NFR-4 | Parcial | Montajes en Docker Desktop (C-1) |
| NFR-5 | Parcial | Tokens por Etapa del Analista y los Revisores (H-3) |
| NFR-6 | Cubierto | AD-9 |

## Puntos fuertes (mantener)

- AD-1 con prueba de arquitectura en CI: la regla es verificable.
- AD-2 + AD-3: un único dueño del Estado y una única fuente para el Panel, con la transacción conjunta.
- AD-8 con un guardián único contra push y merge: cumple FR-17 de forma demostrable.
- AD-9: política pura con entrada cerrada y tests de tabla.
- AD-10: la regla "el repositorio solo restringe" evita escaladas desde `codenetix.yml`, que es contenido no confiable.
- *Deferred* bien justificado en general: ninguna entrada, salvo la licencia (L-6), permite que dos unidades diverjan.
