---
title: CodeNetix
status: final
created: '2026-10-26'
updated: '2026-10-26'
inputs:
  - _bmad-output/planning-artifacts/briefs/brief-Codenetix-2026-10-26/brief.md
  - _bmad-output/planning-artifacts/briefs/brief-Codenetix-2026-10-26/addendum.md
  - _bmad-output/planning-artifacts/research/competitive-codenetix-2026-10-26/research.md
---

# PRD: CodeNetix
*Nombre de trabajo confirmado en el brief.*

## 0. Propósito del documento

Este PRD convierte el *Product Brief* de CodeNetix en requisitos verificables para la v1 (MVP). Lo leen el PM, la arquitectura (Winston), la UX (Sally) y quien cree las épicas y las historias. El vocabulario está fijado en el §3 Glosario y se usa sin sinónimos. Las funcionalidades se agrupan en el §4 con requisitos funcionales (FR) numerados de forma global. Las suposiciones van marcadas con `[ASSUMPTION]` y reunidas en el §11. Las decisiones técnicas (el *cómo*) viven en `addendum.md` y en el addendum del brief, no aquí.

## 1. Visión

CodeNetix es una **AI Software Factory open source y autoalojada**. Convierte eventos (una issue de GitHub, un error de Sentry) en cambios de código revisados, mediante una cadena de montaje con reglas: el **Analista** escribe la **Spec**, el **Programador** implementa con tests en un **Sandbox**, varios **Revisores** independientes emiten un **Veredicto** y la **Política de merge** decide el destino. Un **Panel** en vivo muestra cada paso.

El valor no está en "otro agente que programa": CodeNetix orquesta agentes que ya existen (Claude Code en la v1) y aporta lo que falta en el mercado, que es **gobierno, trazabilidad y confianza**. Todo funciona en la máquina del usuario, con la API key del usuario, sin cuentas ni login y sin coste para el mantenedor.

**Por qué ahora:** en 2026 la *software factory* se ha vuelto tendencia (Factory, Vercel, Devin, GitHub). Pero toda la oferta se detiene en "abrir un PR", vive en la nube del proveedor y nadie atiende a los equipos .NET. Ver `research.md`.

## 2. Usuario objetivo

### 2.1 Jobs To Be Done

- **Funcional:** "Cuando salta un error o entra una tarea sencilla, quiero que se convierta en un PR revisado sin que yo tenga que hacer de pegamento entre herramientas."
- **Control:** "Quiero automatizar sin perder el control: saber qué hizo cada agente, por qué y con qué reglas se decide el merge."
- **Privacidad:** "Mi código no puede acabar en la nube de un tercero que yo no haya elegido."
- **Coste:** "Quiero pagar solo los tokens que consumo, con mi propia key, sin licencias por desarrollador."
- **Social (desarrollador independiente):** "Quiero tener mi propia factoría y poder enseñarla."

### 2.2 No usuarios (v1)

- Equipos cuyos repositorios no son .NET.
- Equipos en Azure DevOps o GitLab sin repositorio en GitHub (la v1 solo trabaja con GitHub).
- Quien busca un copiloto conversacional en el IDE. CodeNetix no es un chat.
- Organizaciones que necesitan SSO, roles o auditoría centralizada: eso será la futura edición Enterprise.

### 2.3 Recorridos clave del usuario

- **UJ-1. Diego prueba CodeNetix en 10 minutos.**
  - **Persona y contexto:** Diego, desarrollador .NET freelance, vio un vídeo sobre *software factories* y quiere la suya sin montarla a mano.
  - **Estado inicial:** tiene Docker instalado, una API key de Anthropic y un token de GitHub. Nunca ha usado CodeNetix.
  - **Recorrido:**
    1. Ejecuta el comando `docker run` del README, pasando sus keys como variables de entorno.
    2. Abre `http://localhost:5000` y ve el Panel vacío, con un mensaje de bienvenida que muestra el comando de ejemplo.
    3. Ejecuta `codenetix fix https://github.com/diego/facturas-api/issues/12`.
    4. En el Panel ve encenderse los nodos uno a uno (Analista, Programador, Revisores) mientras la terminal en vivo muestra la salida.
  - **Clímax:** el Panel marca la Ejecución como "PR creado" con un enlace. En el PR están la Spec, el diff, los tests y los tres Veredictos.
  - **Resolución:** Diego revisa el PR en GitHub y lo fusiona él mismo.
  - **Caso límite:** si `dotnet test` falla tras los reintentos, la Ejecución termina en "Fallida". El Panel muestra la causa y no se crea ningún PR.

- **UJ-2. Lucía deja que los errores de Sentry lleguen solos.**
  - **Persona y contexto:** Lucía, tech lead .NET en una aseguradora, tiene CodeNetix en un servidor interno con webhooks de Sentry y GitHub configurados.
  - **Recorrido:**
    1. Un error `NullReferenceException` se dispara en producción y Sentry envía el webhook.
    2. CodeNetix lo reconoce, lo deduplica y lo asocia al repositorio configurado en `codenetix.yml`.
    3. El Analista escribe una Spec con la traza del error y el Programador implementa el arreglo y un test que reproduce el error.
    4. El Revisor de seguridad aprueba, el de calidad aprueba y el de tests pide un cambio. Tras una Ronda de revisión, los tres aprueban.
  - **Clímax:** a la mañana siguiente, Lucía tiene en GitHub un PR etiquetado `codenetix`, con un aviso de que **toca una Ruta protegida** (`Domain/**`) y requiere su aprobación.
  - **Resolución:** lo aprueba y lo fusiona. En el Panel ve el historial de la Ejecución y su coste estimado en tokens.
  - **Caso límite:** si llegan 40 eventos del mismo error, solo se crea una Ejecución.

- **UJ-3. Marcos añade CodeNetix a su repositorio como GitHub Action.**
  - **Persona y contexto:** Marcos mantiene una librería .NET open source y no quiere ningún servidor.
  - **Recorrido:**
    1. Añade el workflow de ejemplo y su API key como *secret* del repositorio.
    2. Pone la etiqueta `codenetix` a una issue.
    3. La Action ejecuta la cadena completa en el runner de GitHub.
  - **Clímax:** aparece un PR con la Spec, los Veredictos y el resumen de la Ejecución.
  - **Resolución:** no hay Panel en vivo en este modo; la traza completa queda en el log de la Action y en el cuerpo del PR.

## 3. Glosario

- **Evento**: notificación externa que puede iniciar trabajo (una issue de GitHub etiquetada, un error de Sentry o un comando directo). Un Evento pertenece a una sola Fuente.
- **Fuente**: origen de los Eventos. En la v1: GitHub, Sentry y el comando directo (CLI).
- **Repositorio objetivo**: repositorio .NET en GitHub donde se aplica el cambio.
- **Configuración de instancia**: configuración global de una instalación de CodeNetix (variables de entorno y un archivo opcional montado). Contiene las keys, los tokens, los secretos de webhook y la correspondencia entre proyectos de Sentry y Repositorios objetivo.
- **Configuración de repositorio**: archivo `codenetix.yml` en la raíz del Repositorio objetivo. Define las Rutas protegidas, los Revisores activos, los modelos y los límites.
- **Ejecución**: una pasada completa de un Evento por la cadena. Tiene un Estado y una lista de Etapas. Cada Evento genera como mucho una Ejecución activa.
- **Estado de Ejecución**: uno de estos valores: `Recibida`, `Analizando`, `Programando`, `Revisando`, `Decidiendo`, `PR creado`, `Fallida`, `Cancelada`.
- **Etapa**: cada paso de la Ejecución (Análisis, Programación, Revisión, Decisión), con su inicio, fin, log y coste.
- **Analista**: agente que convierte un Evento en una Spec.
- **Spec**: especificación técnica de un cambio: problema, causa probable, cambio propuesto, criterios de aceptación y archivos afectados.
- **Programador**: agente que implementa la Spec y sus tests dentro del Sandbox. En la v1 se usa Claude Code.
- **Sandbox**: entorno aislado y efímero donde se clona el Repositorio objetivo y se ejecutan el código y los tests.
- **Revisor**: agente independiente que evalúa el cambio desde una sola Especialidad y emite un Veredicto.
- **Especialidad**: el enfoque de un Revisor. En la v1: `seguridad`, `calidad`, `tests`.
- **Veredicto**: resultado estructurado de un Revisor: `aprobado` / `cambios solicitados` / `rechazado`, con sus hallazgos.
- **Ronda de revisión**: un ciclo en el que el Programador corrige lo que pidieron los Revisores y estos vuelven a evaluar.
- **Política de merge**: reglas deterministas, definidas en la Configuración de repositorio, que deciden el destino del cambio. En la v1 el destino es siempre un PR.
- **Ruta protegida**: patrón de archivos (p. ej. `**/Migrations/**`, `Domain/**`) cuya modificación exige aprobación humana.
- **Panel**: interfaz web local que muestra las Ejecuciones en tiempo real y su historial.
- **Proveedor**: servicio de LLM que el usuario configura con su propia key (*bring your own key*).

## 4. Funcionalidades

### 4.1 Instalación y configuración

**Descripción:** el usuario pone en marcha CodeNetix con un solo comando y sin crear cuentas. La configuración global (keys, tokens) va en variables de entorno; la de cada repositorio, en `codenetix.yml`. Hace posible el UJ-1 y el UJ-2.

#### FR-1: Arranque con un comando
El usuario puede arrancar CodeNetix con un único `docker run` que incluye las variables de entorno. Hace posible el UJ-1.
- Con solo una key de Proveedor y un token de GitHub, el Panel está disponible en `localhost` en menos de 60 s desde que arranca el contenedor (sin contar la descarga de la imagen).
- No existe registro, login ni creación de cuenta.
- El almacenamiento persistente es un archivo local que se crea solo; no hace falta instalar ninguna base de datos.

#### FR-2: Configuración por variables de entorno
El sistema lee de variables de entorno las keys de Proveedor, el token de GitHub, los secretos de webhook y el token opcional de acceso.
- Las keys y los tokens nunca se escriben en el almacenamiento persistente, en los logs ni en el Panel; se muestran enmascarados (`sk-…abcd`).
- Si falta una variable obligatoria, el sistema arranca en modo diagnóstico: el Panel indica qué falta y no se procesan Eventos.

#### FR-3: Configuración de repositorio
El sistema lee `codenetix.yml` del Repositorio objetivo para aplicar las Rutas protegidas, los Revisores activos, los modelos por agente y los límites.
- Si no hay `codenetix.yml`, se aplican valores por defecto documentados (Revisores `seguridad`, `calidad` y `tests`; Rutas protegidas `**/Migrations/**`, `**/*.csproj`, `.github/**`).
- Un `codenetix.yml` inválido hace que la Ejecución termine en `Fallida` con un mensaje claro, sin llegar a llamar a ningún agente.

#### FR-4: Acceso local seguro
Por defecto, el Panel y la API solo aceptan conexiones desde `localhost`.
- Si el usuario lo expone a la red, todas las peticiones al Panel y a la API exigen el token de acceso configurado; sin token configurado, el sistema no arranca expuesto.
- Los endpoints de webhooks (FR-7) quedan exentos del token de acceso, pero exigen siempre una firma válida.

### 4.2 Entrada de Eventos

**Descripción:** CodeNetix acepta Eventos por tres vías para adaptarse a cada usuario: el comando directo (sin fricción), el sondeo periódico o *polling* (sin URL pública) y los webhooks (en tiempo real, para quien tenga servidor). Hace posibles el UJ-1, el UJ-2 y el UJ-3.

#### FR-5: Comando directo
El usuario puede iniciar una Ejecución con `codenetix fix <URL>`, donde la URL es una issue de GitHub o una incidencia de Sentry. Hace posible el UJ-1.
- La Ejecución aparece en el Panel en menos de 2 s desde el comando.
- Una URL no admitida devuelve un error explicativo y no crea ninguna Ejecución.

#### FR-6: Sondeo de GitHub
El sistema puede consultar periódicamente las issues abiertas con la etiqueta configurada (por defecto `codenetix`) y crear una Ejecución por cada issue nueva.
- El intervalo se puede configurar, con 5 minutos por defecto.
- Una issue que ya tiene una Ejecución activa o terminada no genera otra, salvo que se reetiquete.

#### FR-7: Webhooks de GitHub y Sentry
El sistema expone endpoints para webhooks de GitHub (issue etiquetada) y de Sentry (incidencia nueva o en regresión). Hace posible el UJ-2.
- Se verifica la firma de cada webhook; si es inválida, responde 401 y no se procesa.
- Responde 2xx en menos de 1 s y procesa el Evento de forma asíncrona.
- Se deduplica por ID de entrega y por incidencia, de modo que N eventos del mismo error de Sentry generan como mucho una Ejecución activa.

#### FR-8: Correspondencia entre Sentry y el repositorio
El sistema asocia cada proyecto de Sentry con un Repositorio objetivo según la Configuración de instancia (nunca según `codenetix.yml`, que vive dentro del repositorio).
- Un Evento de Sentry sin correspondencia queda registrado como "ignorado", con su motivo visible en el Panel.

#### FR-26: Autorización del disparador
Solo los Eventos iniciados por personas autorizadas generan Ejecuciones, porque el texto de una issue o de un error es contenido no confiable que llega a un agente capaz de ejecutar comandos.
- Una issue de GitHub solo se procesa si quien puso la etiqueta (o hizo la mención) tiene permiso de escritura en el Repositorio objetivo; si no, el Evento queda "ignorado: no autorizado".
- El comando directo (FR-5) se considera autorizado porque lo ejecuta el operador de la instancia.
- El texto de los Eventos se entrega a los agentes delimitado como datos, nunca como instrucciones del sistema.

### 4.3 Análisis (Spec)

**Descripción:** el Analista lee el Evento (el texto de la issue, o la traza y el contexto del error de Sentry) junto con el código relevante, y redacta la Spec.

#### FR-9: Generación de la Spec
El Analista genera una Spec con: problema, causa probable, cambio propuesto, criterios de aceptación verificables y archivos afectados.
- La Spec cumple un esquema estructurado; una salida que no lo cumple se reintenta como máximo 2 veces y, si sigue fallando, la Ejecución termina en `Fallida`.
- La Spec es visible en el Panel y se incluye en el cuerpo del PR.

#### FR-10: Rechazo temprano
El Analista puede marcar un Evento como "no automatizable" (falta información, cambio demasiado grande o fuera de .NET) y terminar la Ejecución en `Fallida` sin programar.
- El motivo se muestra en el Panel y, si la Fuente es GitHub, se publica como comentario en la issue.

### 4.4 Programación

**Descripción:** el Programador (Claude Code en la v1) implementa la Spec y sus tests dentro de un Sandbox aislado sobre un clon del Repositorio objetivo.

#### FR-11: Implementación en el Sandbox
El Programador modifica el código en un Sandbox aislado y efímero, en una rama nueva `codenetix/<id>`.
- Nunca se ejecuta código del repositorio fuera del Sandbox.
- El Sandbox se destruye al acabar la Ejecución, sea cual sea su Estado.

**Notas:** `[NOTE FOR PM]` En modo servidor, crear Sandboxes desde el contenedor de CodeNetix requiere acceso al motor de Docker del host (socket montado o equivalente). Es una decisión de arquitectura con un riesgo de seguridad que hay que documentar y mitigar (para Winston). En el modo GitHub Action se usa el mismo Sandbox Docker sobre el Docker del runner (ver arquitectura, AD-12).

#### FR-27: Red y secretos del Sandbox
El Sandbox solo puede conectarse a los destinos imprescindibles: el registro de paquetes (NuGet), GitHub para clonar y el Proveedor configurado.
- Cualquier otra conexión de salida se bloquea y queda registrada en el log de la Etapa.
- Las keys del Proveedor y el token de GitHub no son legibles por el código del Repositorio objetivo que se ejecuta en el Sandbox (p. ej., durante `dotnet test`).

#### FR-12: Tests obligatorios
Cada cambio incluye al menos un test que cubre los criterios de aceptación de la Spec.
- `dotnet build` y `dotnet test` deben pasar en el Sandbox para avanzar a la Revisión.
- Si fallan, el Programador reintenta hasta el límite configurado (3 por defecto); al agotarlo, la Ejecución termina en `Fallida`.

#### FR-13: Límite de coste por Ejecución
El usuario puede fijar un presupuesto máximo de tokens o de coste estimado por Ejecución.
- Al superarlo, la Ejecución se detiene en `Fallida` con el motivo "presupuesto agotado".

### 4.5 Revisión

**Descripción:** cada Revisor evalúa el diff, la Spec y el resultado de los tests desde su Especialidad, sin ver el razonamiento del Programador. Esto evita que un mismo modelo se autoconvenza.

#### FR-14: Revisores independientes
El sistema ejecuta los Revisores activos (por defecto `seguridad`, `calidad` y `tests`), cada uno de forma independiente.
- Cada Revisor recibe solo la Spec, el diff y el resultado de los tests; no recibe el historial del Programador.
- Cada Revisor puede usar un Proveedor o modelo distinto al del Programador, según la Configuración de repositorio.
- Los Revisores aceptan cualquier Proveedor compatible con la API de OpenAI, incluidos modelos locales (p. ej., Ollama o LM Studio), de modo que la revisión puede hacerse sin que el código salga de la máquina del usuario.

#### FR-15: Veredicto estructurado
Cada Revisor emite un Veredicto (`aprobado` / `cambios solicitados` / `rechazado`) con hallazgos localizados (archivo y línea).
- Un Veredicto que no cumple el esquema se reintenta y, si falla, cuenta como `cambios solicitados`.

#### FR-16: Rondas de revisión
Si algún Veredicto es `cambios solicitados`, el Programador corrige y los Revisores vuelven a evaluar, hasta 2 Rondas de revisión.
- Un `rechazado`, o agotar las rondas sin aprobación unánime, termina la Ejecución en `Fallida`, con los Veredictos visibles.

### 4.6 Decisión y entrega

**Descripción:** la Política de merge determina el destino del cambio. En la v1 el destino es **siempre un PR con aprobación humana**; la Política solo clasifica el riesgo para informar al humano.

#### FR-17: PR con traza completa
Cuando la revisión es unánime, el sistema crea un PR en el Repositorio objetivo con la etiqueta `codenetix`. Hace posibles el UJ-1, el UJ-2 y el UJ-3.
- El cuerpo del PR incluye: el enlace al Evento de origen, la Spec, un resumen de los Veredictos con sus hallazgos, el resultado de los tests, la clasificación de riesgo y el coste estimado.
- El sistema nunca hace merge ni push a la rama por defecto.

#### FR-18: Clasificación de riesgo
La Política de merge clasifica cada cambio como `menor` o `requiere atención`, mediante reglas deterministas: si toca Rutas protegidas, el tamaño del diff según un umbral configurable y si hubo unanimidad en la primera ronda.
- La misma entrada produce siempre la misma clasificación; ningún LLM interviene en ella.
- Los cambios `requiere atención` lo indican en el título y en el cuerpo del PR, junto con las rutas implicadas.

**Notas:** el auto-merge de los cambios `menor` está en `[NON-GOAL for MVP]` y se activará por repositorio en una versión posterior.

#### FR-19: Notificación en la Fuente
Al terminar la Ejecución, el sistema comenta en la issue de GitHub de origen el resultado: el enlace al PR o el motivo del fallo.

### 4.7 Panel en vivo

**Descripción:** interfaz web local que hace visible la cadena de montaje. Es la pieza de confianza y de demostración del producto. Hace posibles el UJ-1 y el UJ-2.

#### FR-20: Vista de cadena de montaje
El Panel muestra cada Ejecución activa como un grafo de Etapas, con el nodo activo resaltado.
- Los cambios de Estado se reflejan en el Panel en menos de 1 s, sin recargar la página.

#### FR-21: Terminal en vivo
El usuario puede ver en directo la salida de la Etapa activa (agente, comandos y tests).
- Las keys y los tokens aparecen enmascarados también en esta salida (ver FR-2).

#### FR-22: Historial de Ejecuciones
El usuario puede consultar las Ejecuciones pasadas con su Estado, Spec, Veredictos, enlace al PR, duración y coste estimado.
- El historial se conserva tras reiniciar el contenedor, mientras el volumen de datos persista.

#### FR-23: Cancelar una Ejecución
El usuario puede cancelar una Ejecución activa desde el Panel.
- El Estado pasa a `Cancelada`, el Sandbox se destruye y no se crea ningún PR.

### 4.8 Fiabilidad de la Ejecución

#### FR-24: Reanudación tras reinicio
Si CodeNetix se reinicia con Ejecuciones activas, al arrancar las marca como `Fallida` con el motivo "interrumpida". La reanudación desde la última Etapa completada queda para una versión posterior.
- Ningún Evento ya aceptado se pierde por un reinicio: los Eventos aceptados que aún no habían iniciado su Ejecución se procesan al arrancar.

### 4.9 Modo GitHub Action

**Descripción:** CodeNetix se puede ejecutar como un paso de GitHub Actions, sin servidor. Hace posible el UJ-3.

#### FR-25: Ejecución como Action
El usuario puede añadir CodeNetix a un workflow que se dispara cuando se etiqueta una issue y que ejecuta la cadena completa en el runner.
- La key del Proveedor se lee de los *secrets* del repositorio.
- En este modo no hay Panel; la traza completa va al log de la Action y al cuerpo del PR.
- La documentación advierte de que los *secrets* no están disponibles en workflows disparados desde forks.
- En este modo la única Fuente es GitHub (issue etiquetada); Sentry requiere el modo servidor.
- En este modo se usa el mismo Sandbox Docker que en modo servidor, sobre el Docker del runner (requiere un runner `ubuntu-*`), para cumplir también FR-27.

## 5. Requisitos no funcionales transversales

- **NFR-1 Seguridad del código ejecutado:** todo código generado o del repositorio se ejecuta solo dentro del Sandbox, sin acceso al sistema de archivos del host ni a los secretos de CodeNetix (ver FR-27).
- **NFR-2 Privacidad:** el código del usuario solo sale de su infraestructura hacia GitHub y hacia los Proveedores que él mismo haya configurado. No hay telemetría.
- **NFR-3 Coste cero para el mantenedor:** ninguna funcionalidad requiere servicios alojados por el proyecto. No se incluye ninguna key por defecto.
- **NFR-4 Portabilidad:** la imagen funciona en Linux, macOS y Windows (Docker Desktop) en x64 y arm64.
- **NFR-5 Observabilidad:** cada Etapa registra su inicio, fin, duración, tokens y errores, y estos datos se pueden consultar en el Panel.
- **NFR-6 Determinismo de la política:** la Política de merge es reproducible y se prueba con tests unitarios.

## 6. Restricciones y salvaguardas

- **Coste:** el usuario paga los tokens a su Proveedor. El sistema muestra el coste estimado por Ejecución y respeta el presupuesto (FR-13).
- **Seguridad:** sin auto-merge en la v1 y sin push a la rama por defecto (FR-17).
- **Legal:** la licencia y el CLA están pendientes y deben decidirse antes del primer commit público (ver §10). Se respetan los términos de uso de los agentes orquestados: cada usuario usa su propia key y no se usa la marca "Claude Code" como nombre de producto.

## 7. No objetivos (explícitos)

- CodeNetix **no es** un asistente de chat ni un plugin de IDE.
- **No** construye su propio agente programador; orquesta agentes existentes.
- **No** ofrece un servicio en la nube, cuentas de usuario ni login en la edición Community.
- **No** decide el merge con un LLM; la Política es siempre determinista.
- **No** sustituye a Sentry ni a GitHub; se conecta a ellos.

## 8. Alcance del MVP

### 8.1 Incluido, con orden de corte

Solo repositorios .NET alojados en GitHub. Si el tiempo aprieta, se recorta de abajo hacia arriba.

- **P0, núcleo que demuestra el valor:** FR-1 a FR-5 (instalación, configuración, acceso, comando directo), FR-9 a FR-12 (Spec, Sandbox, tests), FR-14 a FR-17 (Revisores, Veredictos, Rondas, PR), FR-20 y FR-21 (cadena de montaje y terminal en vivo), FR-26 y FR-27 (autorización, red y secretos).
- **P1, operación real:** FR-6 a FR-8 (sondeo, webhooks, Sentry), FR-13 (presupuesto), FR-18 y FR-19 (clasificación de riesgo, notificación), FR-22 y FR-23 (historial, cancelación).
- **P2, comodidad y distribución:** FR-24 (gestión de reinicios), FR-25 (modo GitHub Action).

### 8.2 Fuera del MVP
- **Auto-merge** de cambios `menor` (v2, activable por repositorio).
- **Jira** como Fuente (v1.1).
- **Revisores de rendimiento e interfaz** (v2: el de interfaz requiere capturas o visión).
- **Codex y OpenHands** como Programador alternativo (v1.x).
- **Fallos de CI/CD** como Fuente y **optimización de pipelines** (hoja de ruta).
- **Azure DevOps, GitLab y Linear** (hoja de ruta). `[NOTE FOR PM]` Muchos equipos .NET empresariales usan Azure DevOps, así que es el primer candidato tras la v1.
- **Lenguajes distintos de .NET.**
- **Edición Enterprise** (SSO, auditoría, Postgres, políticas centralizadas).

## 9. Métricas de éxito

**Principales**
- **SM-1 Tiempo hasta el primer PR:** desde `docker run` hasta el primer PR creado en un repositorio de ejemplo, en menos de 10 minutos para un usuario nuevo. Valida FR-1, FR-5, FR-17.
- **SM-2 Tasa de PR útiles:** al menos el 50 % de los PRs creados se fusionan sin cambios manuales en los repositorios piloto. Valida FR-9, FR-12, FR-14 a FR-16.
- **SM-3 Adopción:** 1.000 estrellas en GitHub en los 6 meses posteriores al lanzamiento.

**Secundarias**
- **SM-4 Interés comercial:** 3 empresas interesadas en servicios o soporte en los 12 meses siguientes.
- **SM-5 Coste medio por Ejecución:** visible y estable, con objetivo a fijar tras el piloto. Valida FR-13.
- **SM-6 Valor del gobierno:** porcentaje de Ejecuciones en las que al menos un Revisor pidió cambios que el humano, al revisar el PR, considera justificados. Objetivo inicial: al menos el 20 % en los repositorios piloto. Mide el diferencial de CodeNetix frente a "un agente que abre PRs". Valida FR-14 a FR-16.

**Contramétricas (no optimizar)**
- **SM-C1 Número de PRs creados:** no hay que maximizarlo; muchos PRs malos destruyen la confianza. Contrapesa SM-2.
- **SM-C2 Tasa de Ejecuciones exitosas a cualquier precio:** no se relajan los Revisores ni los tests para "aprobar más". Contrapesa SM-2.
- **SM-C3 Estrellas por marketing:** las estrellas sin uso real no validan nada. Contrapesa SM-3.

## 10. Preguntas abiertas

1. **Licencia y CLA**: AGPL, FSL o Apache con Enterprise comercial. Es bloqueante antes del primer commit público. Hay que validarlo con un abogado y registrar la marca.
2. **Repositorios piloto para SM-2**: se usarán proyectos .NET propios de Longu; hay que concretar cuáles antes del primer sprint de validación.
3. **Riesgo OpenHands**: comprobar si tiene o prepara revisores múltiples o políticas de merge.
4. **Contacto con Eric Risco** y crédito público en el README.

## 11. Índice de suposiciones

Todas las suposiciones del borrador las confirmó Longu el 2026-10-26: sondeo cada 5 minutos (FR-6), 3 reintentos (FR-12), 2 Rondas de revisión (FR-16) y, en el modo GitHub Action, solo issues de GitHub como Fuente (FR-25).
