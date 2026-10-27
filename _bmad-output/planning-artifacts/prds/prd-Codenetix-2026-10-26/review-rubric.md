# PRD Quality Review — CodeNetix

## Overall verdict
El PRD tiene una tesis clara y defendible (gobierno, trazabilidad y confianza por encima de "otro agente más"), un glosario sólido, FRs con IDs limpios y consecuencias mayoritariamente medibles, y decisiones duras tomadas de frente (siempre PR, Política determinista, solo .NET en GitHub). Lo que está en riesgo es la base de seguridad y despliegue que sostiene esa tesis: el Sandbox, la *prompt injection* desde issues públicas, la exposición de webhooks frente al token de acceso y la correspondencia Sentry→repositorio tienen contradicciones o huecos que arquitectura heredará sin resolver. Además, el alcance del MVP (cuatro vías de entrada, modo Action, reanudación, Panel en vivo) no está priorizado para un único desarrollador.

## Decision-readiness — adequate

Las decisiones importantes están escritas como decisiones, no como "consideraciones". En la v1 el destino es siempre un PR (§4.6, FR-17). La Política no usa LLM (FR-18, §7). Solo se admite .NET en GitHub (§2.2, §8.1). El auto-merge queda como `[NON-GOAL for MVP]` (FR-18). El §8.2 dice qué se aplaza y a qué versión. Las Preguntas abiertas (§10) son preguntas de verdad: la licencia está marcada como bloqueante y tiene un responsable implícito.

Falta poner nombre a lo que se sacrifica en las decisiones más caras. Para un único mantenedor, ofrecer cuatro vías de entrada (CLI, sondeo, webhooks y Action) es una apuesta de alcance fuerte, y el PRD no explica por qué todas entran en la v1 ni cuál se recortaría primero. Hay solo un `[NOTE FOR PM]` (Azure DevOps, §8.2), puesto en un punto seguro. Las tensiones reales (seguridad del Sandbox y capacidad de un solo desarrollador) no tienen ninguno. FR-24 deja la decisión abierta dentro del propio requisito: "las reanuda ... **o** las marca como `Fallida`".

### Findings
- **[high]** Alcance del MVP sin orden de corte para un solo desarrollador (§8.1) — Los 25 FRs entran todos en el MVP sin prioridad interna. No se dice qué es imprescindible para el lanzamiento público y qué puede salir en la v1.0.x si el calendario se alarga. *Fix:* añadir dentro del §8.1 un "corte de lanzamiento" (por ejemplo, CLI + Panel + Revisores + PR imprescindibles; sondeo, webhooks de Sentry, modo Action y FR-24 recortables) y un `[NOTE FOR PM]` sobre la capacidad de un solo desarrollador.
- **[high]** FR-24 no decide (§4.8) — "las reanuda desde la última Etapa completada o las marca como `Fallida`" son dos comportamientos con un coste de implementación muy distinto: la reanudación exige guardar el estado de cada Etapa y recrear el Sandbox. *Fix:* elegir uno para la v1 (lo más realista es marcar como `Fallida` con el motivo "interrumpida" y re-encolar el Evento) y aplazar la reanudación de forma explícita.
- **[medium]** Faltan `[NOTE FOR PM]` en las tensiones reales — Nada señala el conflicto entre "Sandbox aislado" y "un único `docker run`", ni el riesgo de *prompt injection* (ver Done-ness). *Fix:* marcar esas tensiones de forma explícita en lugar de dejar que arquitectura las descubra.

## Substance over theater — adequate

Los tres protagonistas (Diego, Lucía y Marcos) se corresponden cada uno con un modo de despliegue distinto (local+CLI, servidor+webhooks, Action) y con FRs concretos. No es teatro de personas. La sección "Por qué ahora" (§1) dice algo concreto: la competencia se detiene en "abrir un PR", vive en la nube y no atiende a .NET. Las contramétricas (§9) están bien pensadas. Varios FRs llevan umbrales propios del producto (60 s, 2 s, 1 s, 401, 3 reintentos, 2 Rondas).

Las NFRs mezclan umbrales reales con adjetivos. NFR-1 termina con "salvo los mínimos necesarios", que es justo lo que habría que definir. NFR-5 y NFR-6 están bien. No hay ninguna NFR de recursos ni de concurrencia: cuántas Ejecuciones simultáneas se admiten, y qué límites de CPU, memoria o tiempo tiene un Sandbox. Es la NFR que un producto que ejecuta código arbitrario más necesita. El JTBD "Social" (§2.1) no guía ninguna decisión del PRD.

### Findings
- **[high]** NFR-1 vacía en su punto crítico (§5) — "sin acceso ... a los secretos de CodeNetix, salvo los mínimos necesarios". Pero el Programador (Claude Code) se ejecuta dentro del Sandbox con la API key del Proveedor (addendum: "`claude -p` dentro del Sandbox"), y en ese mismo Sandbox corre `dotnet test` con código generado. Ese código puede leer la key. *Fix:* enumerar qué secretos entran en el Sandbox (API key del Proveedor, sí o no; token de GitHub, no), con qué permisos y qué acceso a red tiene el Sandbox (por ejemplo, solo el endpoint del Proveedor y NuGet).
- **[medium]** Falta una NFR de recursos y concurrencia (§5) — No hay límites de Sandbox (tiempo de pared, CPU, memoria) ni un máximo de Ejecuciones simultáneas. Eso afecta a FR-6/FR-7 (ráfagas de Eventos) y a NFR-4 (Docker Desktop en portátiles). *Fix:* añadir una NFR con valores por defecto (por ejemplo, 1 Ejecución activa por Repositorio objetivo, N globales y un timeout de Sandbox de 30 min).
- **[low]** JTBD "Social" decorativo (§2.1) — No guía ningún FR ni ninguna SM. *Fix:* quitarlo o vincularlo a algo concreto (por ejemplo, a la capacidad de demostración del Panel, §4.7).

## Strategic coherence — adequate

La tesis es explícita (§1: "el valor no está en otro agente que programa ... gobierno, trazabilidad y confianza") y las funcionalidades la sirven: Revisores independientes sin el razonamiento del Programador (§4.5), Política determinista (FR-18), PR con traza completa (FR-17) y Panel como "pieza de confianza" (§4.7). El tipo de MVP es de *experiencia/confianza* y el alcance lo respeta: nada de auto-merge y siempre un humano en el bucle.

Las métricas validan la tesis solo en parte. SM-2 (el 50 % de los PRs se fusionan sin cambios) mide la calidad del resultado, pero se calcula sobre "repositorios piloto" que son proyectos del propio Longu (§10.2), lo que introduce un sesgo de selección. Ninguna SM mide el diferencial que defiende el PRD: que los Revisores atrapen problemas, o que la clasificación de riesgo acierte. SM-3 (estrellas) es una métrica de actividad, aunque SM-C3 la equilibra con honestidad. NFR-2 (sin telemetría) hace que SM-2 y SM-5 solo se puedan medir en el piloto, y el PRD no lo reconoce.

### Findings
- **[medium]** Ninguna métrica valida el diferencial de "gobierno" (§9) — La tesis dice que la revisión multi-agente y la Política aportan confianza, pero no se mide. *Fix:* añadir una SM como "% de Ejecuciones en las que al menos un Revisor pidió cambios que luego se corrigieron" o "% de PRs `requiere atención` que el humano modificó o rechazó", calculada a partir del historial local (FR-22).
- **[medium]** SM-2 y SM-5 sin forma de medirse fuera del piloto (§9 frente a NFR-2) — Sin telemetría, la tasa de PRs útiles de los usuarios reales no se puede conocer. *Fix:* declarar de forma explícita que SM-2 y SM-5 se miden solo en los repositorios piloto y concretar el tamaño mínimo de la muestra (por ejemplo, ≥ 30 PRs en ≥ 3 repositorios), además de dar a SM-5 un objetivo provisional en lugar de "a fijar".
- **[low]** SM-4 sin mecanismo (§9) — "3 empresas interesadas" no dice cómo se registra ese interés. *Fix:* definir el canal (formulario o correo de contacto en el README).

## Done-ness clarity — adequate

La mayoría de los FRs tienen al menos una consecuencia verificable, y varias son excelentes: FR-1 (< 60 s, sin BD), FR-7 (401, 2xx < 1 s, deduplicación), FR-11 (el Sandbox se destruye en cualquier Estado), FR-18 ("la misma entrada produce siempre la misma clasificación") y FR-23. Aun así, hay huecos que la creación de historias va a notar, y algunos son de seguridad.

### Findings
- **[critical]** *Prompt injection* desde el texto de Eventos sin tratar (§4.3, §4.4, §5) — El Analista y el Programador consumen texto de issues y trazas de Sentry, que son contenido no confiable en un proyecto open source, y el Programador tiene acceso a ejecutar comandos y a la API key. Ningún FR ni NFR lo menciona. En un lanzamiento público es el primer vector que se va a probar. *Fix:* añadir un FR o NFR: el contenido del Evento se trata como dato no confiable; solo se procesan issues etiquetadas por usuarios con permiso de escritura (verificable en FR-6/FR-7/FR-25); el Sandbox no tiene red salvo en una lista permitida, y los Revisores no ejecutan nada.
- **[high]** FR-4 contradice a FR-7 (§4.1, §4.2) — FR-4: "si el usuario lo expone a la red, **todas** las peticiones exigen el token de acceso". GitHub y Sentry no pueden enviar ese token; se autentican por firma (FR-7). El UJ-2 (Lucía con webhooks) queda bloqueado si se lee al pie de la letra. *Fix:* en FR-4, excluir los endpoints de webhook del token de acceso porque se autentican por firma, y exigir que el secreto de webhook esté configurado para aceptarlos.
- **[high]** FR-12 no es verificable tal como está (§4.4) — "al menos un test que cubre los criterios de aceptación de la Spec" no dice quién comprueba esa cobertura ni cómo. *Fix:* reformular como "el diff añade o modifica al menos un test; el Revisor `tests` verifica explícitamente cada criterio de aceptación de la Spec y su Veredicto lo enumera".
- **[medium]** Criterios indefinidos en FR-10 y FR-18 (§4.3, §4.6) — "cambio demasiado grande" (FR-10) y "tamaño del diff según un umbral configurable" (FR-18) no tienen valores por defecto. *Fix:* fijar los valores por defecto (por ejemplo, > 10 archivos o > 400 líneas) y hacer que FR-10 use el mismo umbral.
- **[medium]** FR-15 sin límite de reintentos (§4.5) — "se reintenta" sin número, mientras que FR-9 dice "como máximo 2". *Fix:* "como máximo 2 veces", igual que FR-9.
- **[medium]** FR-13: "coste estimado" sin fuente (§4.4) — No se dice cómo se estima el coste (tabla de precios por modelo, actualización). Afecta a FR-13, FR-17, FR-22 y SM-5. *Fix:* definir "Coste estimado" en el glosario (tokens × tabla de precios incluida y configurable) y fijar un presupuesto por defecto.
- **[medium]** FR-5: el CLI no está definido (§4.2, UJ-1 paso 3) — UJ-1 instala solo con `docker run` y en el paso 3 ejecuta `codenetix fix` en el host. No se dice cómo llega el binario `codenetix` al host (¿`docker exec`? ¿una herramienta global de dotnet?). *Fix:* añadir una consecuencia a FR-1 o FR-5 que diga cómo se obtiene e invoca el CLI.
- **[medium]** FR-16: no exige repetir los tests tras una Ronda (§4.5) — Sí se puede deducir, pero conviene decirlo: tras cada corrección se repiten `dotnet build`/`dotnet test` (FR-12) antes de volver a revisar. *Fix:* añadir esa consecuencia a FR-16.
- **[low]** FR-3: "mensaje claro" (§4.1) — Es un adjetivo. *Fix:* "el mensaje indica el archivo, la línea y la clave inválida".

## Scope honesty — adequate

El §7 (No objetivos) y el §8.2 (Fuera del MVP) hacen trabajo real: dicen a qué versión va cada cosa y por qué (por ejemplo, el Revisor de interfaz "requiere capturas o visión"). FR-25 reconoce que los *secrets* no están disponibles en forks. La densidad de puntos abiertos es baja (4 Preguntas abiertas y 1 NOTE FOR PM), lo cual es apropiado para luz verde, con la excepción de la licencia, que está bien señalada como bloqueante.

Lo que falla son las suposiciones *no declaradas*. Las que el PRD sí etiquetó fueron todas confirmadas (§11), pero varias inferencias de peso no llevan etiqueta y nadie las ha confirmado.

### Findings
- **[high]** Suposición oculta: el Sandbox dentro de un único `docker run` (FR-1, FR-11, NFR-1) — Crear Sandboxes efímeros desde un contenedor exige montar el socket de Docker (lo que da acceso root al host y contradice NFR-1) o usar Docker-in-Docker en modo privilegiado. Además, en el modo Action (FR-25), ¿qué es el Sandbox? *Fix:* añadir `[ASSUMPTION]` o `[NOTE FOR PM]` en FR-11 con la decisión de aislamiento que se espera (o pedirla a arquitectura de forma explícita) y decir qué significa "Sandbox" en el modo Action.
- **[medium]** Suposición oculta: Sentry exige una URL pública (FR-7, UJ-2) — El sondeo solo existe para GitHub (FR-6). Para Sentry en la nube, el webhook necesita que CodeNetix sea accesible desde Internet, lo que choca con la promesa de "funciona en tu máquina" (§1) y con FR-4. *Fix:* declararlo (`[ASSUMPTION]` o un No-objetivo: "sin sondeo de Sentry en la v1; Sentry exige exposición o un túnel") o añadir sondeo de Sentry.
- **[low]** Concurrencia y cola no mencionadas — No se dice si los Eventos se encolan ni qué ocurre cuando llegan 10 issues a la vez. *Fix:* cubrirlo en la NFR de concurrencia propuesta más arriba.

## Downstream usability — adequate

Es un PRD que alimenta a otros documentos y en general está bien preparado. El glosario es de buena calidad y los IDs son contiguos y únicos (FR-1..25, UJ-1..3, NFR-1..6, SM-1..5, SM-C1..3). Los FRs citan los UJ a los que dan soporte y los tres UJ tienen un protagonista con nombre. Los problemas están en el modelo de dominio, que es lo que arquitectura va a extraer.

### Findings
- **[high]** Correspondencia Sentry→repositorio circular (§3 "Configuración de repositorio", FR-8, UJ-2 paso 2) — El glosario sitúa la correspondencia entre Fuentes y repositorio en `codenetix.yml`, que vive *dentro* del Repositorio objetivo. Pero para saber qué repositorio leer, primero hay que conocer esa correspondencia. *Fix:* llevar la correspondencia proyecto de Sentry → repositorio a la configuración global (variables de entorno o un archivo de configuración de la instancia) y quitarla de la definición de "Configuración de repositorio".
- **[medium]** Fuente ambigua para `codenetix fix <URL de Sentry>` (§3 "Evento", FR-5, FR-10, FR-19) — El glosario dice que un Evento pertenece a una sola Fuente y enumera "comando directo" como Fuente. Si se lanza la CLI con una URL de una issue de GitHub, ¿la Fuente es CLI o GitHub? FR-10 y FR-19 comentan en la issue "si la Fuente es GitHub". *Fix:* separar "canal de entrada" (CLI, sondeo, webhook) de "Fuente" (GitHub, Sentry), o indicar de forma explícita que FR-19 se aplica a cualquier Evento cuyo origen sea una issue de GitHub.
- **[medium]** Estado "ignorado" fuera del modelo (FR-8 frente a §3) — FR-8 registra Eventos como "ignorado", pero el Evento no tiene estados en el glosario y "ignorado" no está entre los Estados de Ejecución. *Fix:* definir "Estado de Evento" (`aceptado` / `ignorado` / `duplicado`) en el glosario.
- **[low]** Términos usados sin definir — "token de acceso" (FR-2, FR-4), "coste estimado", "rama por defecto", "Etapa activa" y "nodo" (FR-20). *Fix:* añadir al glosario al menos "Token de acceso" y "Coste estimado".

## Shape fit — strong

Es una herramienta de desarrollo open source con una UX real (el Panel, la primera experiencia en 10 minutos), y la forma del documento encaja: tres UJ con protagonista, cada uno ligado a un modo de despliegue, sin exceso de personas, y FRs en formato de especificación de capacidades. La separación entre el *qué* (PRD) y el *cómo* (addendum) se respeta bien. El nivel de rigor es adecuado para un documento que alimenta a UX, arquitectura e historias. Lo único que le falta a UX es una lista de los estados de pantalla del Panel (modo diagnóstico de FR-2, vacío del UJ-1, Evento ignorado de FR-8, Fallida con causa). Están repartidos entre los FRs y se pueden extraer, pero convendría reunirlos.

### Findings
- **[low]** Estados del Panel dispersos (§4.7) — *Fix:* añadir a la descripción del §4.7 una lista breve de los estados de pantalla del Panel que cite los FRs de origen.

## Mechanical notes

- **Referencia cruzada rota:** el §6 "Legal" dice "ver §9" para la licencia, pero la licencia está en el **§10** Preguntas abiertas (el §9 son las Métricas).
- **Ida y vuelta del índice de suposiciones:** el §0 promete suposiciones "marcadas con `[ASSUMPTION]`", pero no hay ninguna etiqueta en el texto. El §11 enumera cuatro suposiciones ya confirmadas (FR-6, FR-12, FR-16, FR-25) sin una marca equivalente en esos FRs. Se puede aceptar si están confirmadas, pero entonces el §0 debería decir que no queda ninguna suposición abierta. Las suposiciones ocultas señaladas en Scope honesty deberían añadirse aquí.
- **Desajustes de vocabulario:** "incidencia" (de Sentry) y "error" se usan como sinónimos en FR-5, FR-7 y UJ-2; "reetiquete" (FR-6) no define si hay que quitar y volver a poner la etiqueta; "revisión unánime" (FR-17) frente a "unanimidad en la primera ronda" (FR-18) es correcto, pero conviene definir "Aprobación unánime".
- **IDs:** FR-1..25, NFR-1..6, SM-1..5, SM-C1..3 y UJ-1..3 son contiguos y no hay duplicados. Las referencias FR→UJ se resuelven bien.
- **Estado del frontmatter:** `status: draft`. Hay que actualizarlo cuando se resuelvan los hallazgos críticos y altos.
- **Nombre del producto:** "CodeNetix" en el texto frente a "Codenetix" en las rutas y el frontmatter de entrada. Hay que decidir qué grafía es la canónica antes de registrar la marca (§10.1).
