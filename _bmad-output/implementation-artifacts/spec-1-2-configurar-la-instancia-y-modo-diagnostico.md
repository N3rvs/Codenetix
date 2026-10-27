---
title: 'Historia 1.2: Configurar la instancia y modo diagnóstico'
type: 'feature'
created: '2026-10-27'
status: 'done'
route: 'dispatch'
baseline_commit: '3f3cd2b4d920dc02b71dace064803db6e9b1a9ad'
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-1-context.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** CodeNetix arranca, pero no lee ninguna clave ni token. Tampoco dice qué le falta, y nada impide que un secreto acabe en un log o en una respuesta de la API (FR-2).

**Approach:** Host carga la Configuración de instancia (`CODENETIX__*` más el archivo opcional `/config/codenetix.instance.yml`) y `StartupReconciler` la valida y publica `IInstanceHealth` (`Ready | Diagnostic(reasons)`). Un único `ISecretRedactor` enmascara los secretos en los logs y en las respuestas de `/api` (AD-13). `/api/health` y el Panel muestran qué falta.

## Boundaries & Constraints

**Always:**
- **Claves** (decisión): token de GitHub en `CODENETIX__GitHub__Token`; Proveedores en `CODENETIX__Providers__<Nombre>__ApiKey` (y `__BaseUrl` opcional). En el YAML van las mismas claves sin el prefijo (`GitHub: { Token: … }`, `Providers: { Anthropic: { ApiKey: … } }`), cargadas bajo la sección `CODENETIX`. El entorno tiene prioridad sobre el archivo. La ruta del archivo por defecto es `/config/codenetix.instance.yml`; `CODENETIX__InstanceConfigPath` la sobrescribe.
- **Obligatorio:** el token de GitHub y al menos un Proveedor con `ApiKey` no vacía. Cada motivo de `Diagnostic` es una frase en español que nombra la variable que falta, nunca un valor.
- **Contracts:** `IInstanceHealth` (`Status`: `Starting | Ready | Diagnostic`, `Reasons`, y `WhenReadyAsync(ct)`, que solo se completa en `Ready`; es la puerta que deben usar todos los `BackgroundService` futuros) e `ISecretRedactor` (`string Redact(string)`). Las implementaciones van en Host. `IInstanceHealth` sustituye a `StartupState`.
- **Orden de `StartupReconciler`** (AD-18): 1) base de datos; 2) validar la configuración y publicar `Ready` o `Diagnostic`.
- **`/api/health`:** sigue siendo anónimo. `Starting` → 503; `Ready` → 200 `{"status":"Ready","reasons":[]}`; `Diagnostic` → 200 `{"status":"Diagnostic","reasons":[…]}`. Diagnostic devuelve 200 a propósito: la instancia está viva y el `HEALTHCHECK` debe dar `healthy`.
- **Redactor:**
  - Conoce todos los valores de la configuración cuya última clave es `ApiKey`, `Token`, `AuthToken`, `AccessToken`, `Secret`, `WebhookSecret` o `Password` (sin distinguir mayúsculas).
  - Además enmascara patrones conocidos: `sk-ant-…`, `sk-…`, `ghp_`/`gho_`/`ghu_`/`ghs_`/`ghr_`… y `github_pat_…`.
  - Máscara: con 12 caracteres o más, los 3 primeros + `…` + los 4 últimos (`sk-…abcd`); con menos, solo `…`.
  - Se aplica en el sumidero de logs (el formateador de consola) y en un middleware que reescribe los cuerpos de respuesta de `/api`. Los productores no redactan.
- **Panel:** consulta `/api/health` y, en `Diagnostic`, muestra el aviso "CodeNetix está en modo diagnóstico" con la lista de motivos; en `Ready` muestra la bienvenida de 1.1.

**Never:** escribir secretos en SQLite, logs, API o Panel; leer el entorno fuera de Host; auth, `admin-token`, `Host__Exposed` (1.3); asignar Proveedores a agentes o precios (1.8 y Épica 5); crear `BackgroundService` reales; añadir dependencias de frontend.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Configuración completa por entorno | `GitHub__Token` y `Providers__Anthropic__ApiKey` | `Ready`; health 200 `Ready`; `WhenReadyAsync` se completa | N/A |
| Falta el token de GitHub | Solo la key de Proveedor | `Diagnostic` con un motivo que nombra `CODENETIX__GitHub__Token` | `WhenReadyAsync` no se completa |
| Faltan ambos | Sin variables | `Diagnostic` con 2 motivos | Arranque normal, health 200 |
| Valor vacío o en blanco | `GitHub__Token=" "` | Cuenta como ausente | — |
| Configuración solo por YAML | Archivo con ambas claves, sin variables | `Ready` | — |
| Entorno y YAML a la vez | Ambos con valores distintos | Gana el entorno | — |
| YAML inválido | Archivo que no se puede parsear | El proceso sale con un código distinto de 0 y un log que nombra la ruta del archivo | Nunca muestra contenido del archivo |
| Secreto en un log | Un log contiene el valor del token | La consola muestra `ghp…wxyz` | — |
| Secreto en la API | Una respuesta de `/api` contiene una key | El cuerpo sale enmascarado | — |
| Patrón no configurado | Un log con `sk-ant-` seguido de 40 caracteres que no está en la configuración | Enmascarado por patrón | — |

</frozen-after-approval>

## Code Map

- `src/CodeNetix.Host/ServeMode.cs` -- composición: añadir la fuente YAML (antes del entorno), las opciones de instancia, el redactor, el formateador de consola redactor y el middleware de `/api`. Las opciones se leen de la sección `CODENETIX` (como `CODENETIX:Persistence` hoy).
- `src/CodeNetix.Host/StartupReconciler.cs` -- añadir el paso 2 tras la BD; hoy llama a `StartupState.MarkCompleted()`.
- `src/CodeNetix.Host/StartupState.cs` -- eliminarlo, porque lo sustituye `IInstanceHealth`.
- `src/CodeNetix.Host/Features/GetHealth/GetHealthEndpoint.cs` -- mapear los tres estados; añadir `reasons` a `HealthResponse`.
- `src/CodeNetix.Host/CodeNetixCommandLine.cs` -- `serve` devuelve `ExitFailure` ante una excepción de arranque (el YAML inválido se apoya en esto).
- `panel/src/App.tsx` -- hoy solo muestra la bienvenida estática.
- `tests/CodeNetix.Host.Tests/CodeNetixFactory.cs` -- inyecta la configuración en memoria (`CODENETIX:Persistence:DataDirectory`); añadir un parámetro para valores extra y apuntar la ruta del YAML a un archivo inexistente por defecto.
- `tests/CodeNetix.Host.Tests/CommandLineTests.cs` -- el patrón `RunAsync(["serve", "--CODENETIX:…=…"])` con captura de stderr y la colección no paralela; se reutiliza para el caso de YAML inválido.
- `Directory.Packages.props` -- versiones centralizadas: añadir aquí la del parser YAML (YamlDotNet; escribir un proveedor de configuración pequeño, sin paquetes de proveedor de terceros).

## Tasks & Acceptance

**Execution:**
- [x] `src/CodeNetix.Contracts/` -- `IInstanceHealth`, `InstanceHealthStatus` e `ISecretRedactor`.
- [x] `src/CodeNetix.Host/Configuration/` -- `InstanceOptions` (GitHub, Providers), la validación que produce los motivos, la fuente YAML con prefijo `CODENETIX` e `InstanceHealth`.
- [x] `src/CodeNetix.Host/Secrets/` -- `SecretRedactor` (secretos conocidos más patrones), `RedactingConsoleFormatter` y el middleware de redacción de `/api`.
- [x] `src/CodeNetix.Host/` -- conectar todo en `ServeMode`, el paso 2 de `StartupReconciler`, el health con tres estados y eliminar `StartupState`.
- [x] `panel/src/App.tsx` -- `fetch('/api/health')` y la vista de diagnóstico con la lista de motivos.
- [x] `tests/CodeNetix.Host.Tests/` -- un test por fila de la matriz; tests unitarios de la tabla de máscaras de `SecretRedactor`; un test que arranca con secretos y busca sus bytes en `codenetix.db`, `-wal` y `-shm` sin encontrarlos.

**Acceptance Criteria:**
- Given un arranque sin variables, when termina `StartupReconciler`, then `IInstanceHealth.Status` es `Diagnostic` y `WhenReadyAsync` sigue pendiente pasados 500 ms.
- Given la imagen arrancada sin variables, when se abre `http://localhost:5000`, then el Panel muestra el modo diagnóstico con los dos motivos y `docker inspect` da `healthy`.
- Given la imagen arrancada con `-e CODENETIX__GitHub__Token=ghp_…`, when se leen `docker logs`, then el valor completo no aparece en ninguna línea.

## Verification

**Commands:**
- `dotnet build CodeNetix.slnx` -- expected: 0 errores y 0 advertencias.
- `dotnet test --solution CodeNetix.slnx` -- expected: todo en verde.
- `npm run build` (en `panel/`) -- expected: compila sin errores de TypeScript.
- `docker build -f docker/codenetix.Dockerfile -t codenetix .` y después `docker run` del humo, sin variables y con variables -- expected: health `Diagnostic` con 2 motivos o `Ready` respectivamente, y `docker logs` sin el valor del token. Al terminar se borran el contenedor y el volumen.

## Implementation Notes

- La ruta por defecto del YAML es opcional; una ruta indicada en `CODENETIX__InstanceConfigPath` que no existe o es un directorio aborta el arranque. Los nulos YAML sin comillas (`~`, `null`, vacío) cuentan como ausentes. Los tests ignoran las `CODENETIX__*` del proceso, salvo los de entorno, que las retiran y las restauran.
- La fuente YAML se inserta justo antes de la fuente de variables de entorno sin prefijo (`AddInstanceConfigFile`), así que entorno y línea de comandos ganan. Un YAML inválido lanza `InstanceConfigurationException` al construir el builder; el mensaje da ruta y línea/columna, nunca contenido. `CodeNetixCommandLine` lo convierte en `ExitFailure`.
- `CodeNetixFactory` pasa sus valores como configuración de host (`CreateHost` → `--clave=valor` al punto de entrada) para que `ServeMode` los vea antes de `Build()`; antes usaba `ConfigureAppConfiguration`, que en minimal hosting se aplica demasiado tarde para resolver la ruta del YAML.
- Redactor: los secretos conocidos de menos de 4 caracteres no se buscan como literal (enmascararían cualquier salida); también se buscan sus formas escapadas en JSON. Los patrones exigen 16+ caracteres tras el prefijo y un límite por la izquierda (no tocan `sk-learn`).
- El formateador de consola reproduce el formato "simple" sin colores. `ServeMode` limpia los proveedores de logging y deja solo la consola redactora.
- El 404 de `/api` incluye ahora `instance` (la ruta, RFC 9457). Sirve de prueba de extremo a extremo del middleware de redacción.
- El middleware de `/api` guarda el cuerpo completo en un búfer. Un endpoint de streaming (SSE en `/api`) necesitará un camino propio.

## Spec Change Log

## Review Triage Log

| # | Hallazgo | Veredicto | Evidencia | Ruta |
|---|----------|-----------|-----------|------|
| 1 | En el YAML, `Token: ~` o `null` llega como el texto `"~"` o `"null"` y da `Ready` falso (blind, edge) | high | `Flatten` guarda `scalar.Value` literal; RepresentationModel no resuelve los nulos planos. Además, un secreto `"null"` haría que el redactor rompiera cada `null` del JSON. | patch |
| 2 | La fuente YAML se inserta antes del primer origen de entorno, que puede ser el prefijado `DOTNET_` (edge) | low | Quedaría por debajo de `appsettings`; hoy no hay appsettings, pero el orden es frágil y el arreglo es directo: insertar antes del origen sin prefijo. | patch |
| 3 | Si `InstanceConfigPath` es explícito y el archivo no existe, se ignora sin avisar (edge) | medium | El operador ve `Diagnostic` sin ninguna pista de la ruta equivocada. | patch |
| 4 | El redactor no capta la forma URL-encoded de un secreto (blind, edge) | low | Kestrel registra la ruta de la petición; con un secreto que tenga `/`, `+` o `=` se escaparía. El arreglo es una línea. | patch |
| 5 | Ningún test del host prueba un secreto configurado que no case con los patrones (blind, verif.) | medium | Los tests e2e usan `sk-ant-`/`ghp_`, que ya enmascara la regex; romper `CollectSecrets` o el cableado no haría fallar nada. | patch |
| 6 | `Starting` → 503 no está probado (blind) | low | Es uno de los tres estados de la spec y el Panel depende de su cuerpo; el test es directo. | patch |
| 7 | Los tests dependen de las variables `CODENETIX__*` del desarrollador (blind) | medium | Con un token definido en la shell, fallan los tests de "falta". | patch |
| 8 | La ruta por defecto `/config/codenetix.instance.yml` no está probada (verif.) | low | Todos los hosts de test sobrescriben la ruta; el test es trivial. | patch |
| 9 | El Panel de diagnóstico no tiene verificación automática (blind, verif.) | medium (sin verificar) | No hay runner de frontend y la spec prohíbe añadir dependencias; hoy solo lo cubre el humo manual. | defer |
| 10 | `IInstanceHealth` sin lectura atómica de estado y motivos (blind) | low | El arreglo cambia la forma del contrato fijada en la intención congelada. | rechazado |
| 11 | Un secreto que sea una palabra corriente (`test`) rompe la salida (edge) | low | Poco probable una vez arreglado el #1; requiere heurística. | rechazado |
| 12 | La página de excepciones de Development salta la redacción (blind, edge) | low | La imagen corre en Production; solo afecta a quien fuerce Development. | rechazado |
| 13 | Cabeceras sin redactar, charset distinto de UTF-8, `HasStarted` con otra longitud (blind, edge) | low | Ningún endpoint actual escribe cabeceras con secretos, usa otro charset ni arranca la respuesta antes. | rechazado |
| 14 | stderr previo al `Build` usa un redactor vacío (blind, edge, verif.) | low | Ninguna excepción de arranque actual lleva valores secretos. | rechazado |
| 15 | Faltan README y `.env.example` con las variables (blind) | low | Los motivos de diagnóstico ya nombran cada variable; la documentación es la historia 6.5. | rechazado |
| 16 | `sprint-status` en `in-progress` frente a la spec en `in-review`; Change Log vacío (blind) | false | El paso 5 sincroniza el estado; el 404 con `instance` es una nota de implementación, no un cambio de spec. | rechazado |
| 17 | Panel: claves React duplicadas, error transitorio y sin sondeo (blind) | low | Los motivos son constantes distintas; el resto es cosmético. | rechazado |
| 18 | `InstanceHealth` permite `MarkDiagnostic` después de `MarkReady` (blind) | low | Solo lo invoca `StartupReconciler`, una vez. | rechazado |
| 19 | Los secretos no se recortan al enlazarlos (blind) | low | `docker -e` y `--env-file` no añaden saltos de línea; el arreglo añade un PostConfigure. | rechazado |
| 20 | Las claves de fusión YAML (`<<`) se ignoran (edge) | low | Uso poco habitual en un archivo de pocas claves. | rechazado |
