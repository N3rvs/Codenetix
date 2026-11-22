# Trabajo aplazado

- source_spec: `_bmad-output/implementation-artifacts/spec-1-1-arrancar-codenetix-con-un-comando.md`
  summary: El comando de ejemplo `docker exec codenetix codenetix fix <url>` (AD-12, bienvenida del Panel) no funciona con el `docker run` documentado en FR-1 y la historia 1.1, que no tiene `--name codenetix`.
  evidence: Docker asigna un nombre aleatorio al contenedor y `docker exec codenetix` falla. Hay que alinear epics, arquitectura y README (añadir `--name codenetix` al comando canónico) como mucho en la historia 1.4, que introduce `codenetix fix`. Severidad medium.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-2-configurar-la-instancia-y-modo-diagnostico.md`
  summary: La vista de diagnóstico del Panel (motivos en `Diagnostic`, bienvenida en `Ready`) no tiene verificación automática.
  evidence: `panel/` no tiene runner de tests y la historia 1.2 prohibía añadir dependencias de frontend; hoy solo lo cubre el humo manual con Docker. Severidad medium (sin verificar). Se resolvería al introducir Vitest/Testing Library en la Épica 4.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-3-acceso-seguro-al-panel-y-la-api.md`
  summary: `AdminTokenMiddleware` solo acepta `Authorization: Bearer`; los clientes SignalR del navegador (WebSocket/SSE) envían el token como `access_token` en la query, así que `/hubs/**` será inaccesible desde el Panel.
  evidence: Limitación del navegador con WebSocket/SSE. Aceptar `access_token` solo en `/hubs/**` y asegurar que la query no llega en claro a los logs, como muy tarde en la historia 4.1 (primer hub). Severidad medium.
  resolved: Historia 4.1 — `AdminTokenMiddleware` acepta `?access_token=<token>` únicamente en `/hubs/**` con comparación en tiempo constante (`FixedTimeEquals`) y sin exponer el token en logs ni respuestas de error.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-6-ejecucion-con-maquina-de-estados-visible.md`
  summary: `RunEventLog` redacta el JSON serializado completo; un secreto configurado que coincida con una clave o valor de enum ("type", "Failed"…) rompe el discriminador.
  evidence: Redactar solo los valores de texto libre antes de serializar. Severidad low.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-6-ejecucion-con-maquina-de-estados-visible.md`
  summary: El camino de éxito de `IRunEventSink.AppendAsync` externo (seq = max+1, redacción, reintento por conflicto) no tiene test.
  evidence: Cubrirlo con el primer productor real (1.7: `RepositoryPrepared`/`RepoConfigResolved`). Severidad low.
  resolved: Historia 1.7 — `RunExecutionTests.External_sink_appends_with_the_next_seq_and_redacts_the_payload` y `External_sink_retries_with_a_new_seq_when_another_writer_takes_it`.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-7-clonar-y-leer-la-configuracion-del-repositorio.md`
  summary: La comprobación posterior de credenciales solo se ejercita con clones `file://` (que ignoran `http.extraHeader`); falta un clon HTTP con éxito.
  evidence: Servir un repositorio smart-HTTP (`git http-backend`) en el fake. Severidad low.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-7-clonar-y-leer-la-configuracion-del-repositorio.md`
  summary: Con `CloneBaseUrl` `http://` el token viaja en claro en `Authorization: Basic`.
  evidence: Rechazar el token con `http` salvo loopback. Severidad low.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-7-clonar-y-leer-la-configuracion-del-repositorio.md`
  summary: Si el proceso cae entre el guardado terminal y el `finally`, `workspaces/<shortId>` queda en disco.
  evidence: Limpiar árboles huérfanos en la reconciliación al arrancar (5.3). Severidad low.
  resolved: Historia 5.3 — `IRepositoryWorkspace.CleanupOrphansAsync` (en `RepositoryWorkspace`: solo directorios `<shortId>`, sin seguir enlaces y con archivos `ReadOnly` en Windows) se invoca desde `StartupReconciler` en el paso 4 de AD-18; `StartupReconciliationTests.Restart_removes_orphan_workspace_trees_and_leaves_anything_that_is_not_a_run_tree`.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-8-el-analista-genera-la-spec.md`
  summary: `Providers__<Nombre>__BaseUrl` con `http://` a un host no local envía la key del Proveedor en claro sin pasar a `Diagnostic`.
  evidence: Aceptar `http` solo para loopback/red privada. Severidad low. Agrupar con el mismo caso de `CloneBaseUrl` (1.7).

- source_spec: `_bmad-output/implementation-artifacts/spec-1-8-el-analista-genera-la-spec.md`
  summary: El listado de archivos para el Analista se trunca a las 2000 primeras rutas ordenadas, que pueden excluir `src/`.
  evidence: Priorizar extensiones/directorios de código antes de truncar. Severidad low.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-9-rechazo-temprano-con-aviso-en-la-issue.md`
  summary: Notificaciones `Pending`/`Failed` (cancelación, 201 ilegible) no se reintentan ni dejan evento.
  evidence: La reconciliación de 5.3 debe recorrer `gh_notifications` no `Posted` de Ejecuciones terminales. Severidad low.

- source_spec: `_bmad-output/implementation-artifacts/spec-2-1-sandbox-efimero-por-ejecucion.md`
  summary: `DOCKER_HOST` `tcp://` con TLS (`DOCKER_TLS_VERIFY`/`DOCKER_CERT_PATH`) no está soportado y se reporta como "Docker no responde".
  evidence: Detectar esas variables y dar un motivo explícito en `Diagnostic` (o soportar TLS de cliente). Severidad low.

- source_spec: `_bmad-output/implementation-artifacts/spec-2-3-el-programador-implementa-la-spec.md`
  summary: `ClaudeCodeAgent.BuildCommand` todavía no pasa `--max-budget-usd <restante>` al CLI de Claude Code (AD-5, AD-15).
  evidence: El presupuesto restante depende de `IBudgetLedger`, que se implementa en la Épica 5 (historia 5.2). Añadir la bandera en cuanto exista el libro de presupuesto. Severidad medium.
  resolved: Historia 5.2 — `ClaudeCodeAgent` consulta `IBudgetLedger.GetRunCostAsync`, calcula el presupuesto restante respecto a `EffectiveConfig.BudgetUsd` y pasa `--max-budget-usd <restante>` (`CultureInfo.InvariantCulture`) en `BuildCommand` (devolviendo `CodingResult.BudgetExhausted` si el restante es `<= 0`).
