---
title: 'Product Brief: CodeNetix'
status: final
created: '2026-10-26'
updated: '2026-10-26'
inputs:
  - _bmad-output/planning-artifacts/research/competitive-codenetix-2026-10-26/research.md
---

# Product Brief: CodeNetix

> La capa de gobierno open source y autoalojada que convierte errores, tareas y builds rotos en código revisado, sin perder el control.

## Resumen ejecutivo

Los agentes de IA ya escriben código, pero quien los usa sigue siendo el cuello de botella: copia el error de Sentry, abre el agente, revisa lo que hizo y decide si se fusiona. **CodeNetix es una "AI Software Factory" open source**. Escucha eventos de Sentry, Jira, GitHub y del CI/CD, y los hace pasar por una cadena de montaje con reglas: un agente **Analista** escribe la spec, un agente **Programador** implementa con tests, varios **Revisores independientes** auditan seguridad, rendimiento e interfaz, y una **política de merge determinista** decide si el cambio se fusiona solo o necesita aprobación humana. Un panel en tiempo real muestra qué agente trabaja y cómo avanza.

**Por qué ahora:** en 2026 la "software factory" es tendencia. Factory, Vercel, Devin y GitHub empujan en esa dirección y el concepto se ha hecho viral entre desarrolladores. Pero todas las ofertas se quedan en "abrir un PR", viven en la nube del proveedor y cobran entre 20 y 40 $ por desarrollador al mes. Ninguna combina revisores independientes, merge por niveles de riesgo, autoalojamiento con la API key del usuario y soporte nativo de .NET.

CodeNetix **no reinventa el agente que programa**: orquesta los que ya existen (Claude Code, Codex, OpenHands) y aporta lo que falta, que es **gobierno, trazabilidad y confianza**.

## El problema

- **El humano hace de pegamento.** Con un agente, con dos o con cuatro ventanas abiertas, todos esperan a que alguien les diga algo. El tiempo entre "salta el error" y "llega el arreglo revisado" sigue dependiendo de una persona.
- **Las soluciones actuales piden confianza ciega o te encierran.** Seer, Copilot, Devin o el agente de Jira abren PRs, pero ninguno ofrece revisión independiente ni una política clara sobre qué se fusiona solo. Todos funcionan en la nube del proveedor y dentro de su ecosistema (solo GitHub, solo Vercel, solo Jira Cloud).
- **Los equipos .NET empresariales son los peor atendidos.** Trabajan con código privado, a menudo con Azure DevOps y con restricciones para sacar código a terceros, y no hay ninguna herramienta pensada para su stack.
- **Montarlo a mano es posible, pero caro.** Quien vio la factoría de otro y quiere la suya tiene que encadenar scripts, agentes y webhooks por su cuenta.

## La solución

**Instalar y usar:** `docker run`, poner la API key, abrir `localhost` y listo. Sin login, sin servidor de base de datos y sin cuentas.

1. **Entrada:** un error de Sentry, una tarea de Jira, una issue de GitHub o un build roto. Se puede activar por webhook, por *polling* o con un comando directo (`codenetix fix <url>`).
2. **Analista:** convierte el evento en una spec técnica precisa.
3. **Programador:** implementa la spec con tests en un sandbox aislado, usando el agente que elija el usuario.
4. **Revisores:** auditan el cambio de forma independiente (seguridad, rendimiento, interfaz), idealmente con modelos distintos.
5. **Política de merge:** reglas deterministas y versionadas en el repositorio (`codenetix.yml`). Los cambios menores y aprobados por unanimidad pueden fusionarse solos; si se toca la base de datos o la lógica de negocio, se abre un PR para aprobación humana.
6. **Panel en vivo:** la cadena de montaje con el nodo activo iluminado y una terminal en directo. Todo es transparente y auditable.

## Qué lo hace diferente

Con honestidad: **la idea no es única ni defendible por sí sola.** El término "software factory" ya lo domina Factory en buscadores y Vercel publica una plantilla parecida. CodeNetix se diferencia por **la combinación** de estos elementos y por **la velocidad de ejecución**:

| Diferenciador | Situación del mercado (según el análisis de competencia) |
|---|---|
| Política de merge determinista según el riesgo | Ningún competidor revisado la ofrece; todos se detienen en un PR |
| Revisores independientes por especialidad | Solo Vercel Foreman (un revisor); el resto no tiene |
| Autoalojado, *bring your own key*, open source | OpenHands sí; los comerciales solo en Enterprise o nunca |
| Sentry, Jira, GitHub y CI/CD en un solo orquestador | Devin se acerca, pero es cerrado y de pago |
| .NET nativo | Nadie |
| Neutral respecto al agente | El mercado tiende a la interoperabilidad, y CodeNetix se sube a esa ola |

**Competidor más cercano: OpenHands** (MIT, autoalojado, *open core*). El riesgo es que añada revisores y políticas de merge. Por eso la ventaja depende de **lanzar pronto y crear comunidad**.

## A quién sirve

- **Principal:** desarrolladores y equipos .NET con repositorios privados (GitHub o Azure DevOps) que ya usan Sentry y/o Jira, y quieren automatizar arreglos sin ceder el control ni sacar su código a la nube de un tercero.
- **Secundario:** desarrolladores independientes y freelancers que quieren "tener su factoría" sin montarla a mano.
- **Futuro (de pago):** empresas reguladas (banca, seguros, sector público) que necesitan SSO, auditoría y políticas centralizadas.

## Principios no negociables

1. **Cero fricción:** se descarga, se pone una API key y funciona. Sin login, sin servidor de base de datos (SQLite embebido) y sin cuentas.
2. **Cero coste para el mantenedor:** todo se autoaloja y cada usuario usa su propia API key. Nada de keys por defecto, servidores intermediarios centrales, demos públicas con LLM ni telemetría a servidores propios.
3. **La política decide, no el LLM:** el merge lo deciden reglas deterministas y auditables.
4. **El código del usuario no sale de su infraestructura**, salvo hacia el proveedor de LLM que él mismo elija. Admite modelos locales.

## Criterios de éxito

- **Adopción:** primer arreglo funcionando en menos de 10 minutos desde la descarga; 1.000 estrellas en GitHub en los 6 meses posteriores al lanzamiento.
- **Calidad:** al menos el 50 % de los PRs generados se aprueban sin cambios manuales en los repositorios piloto.
- **Confianza:** ningún merge automático que haya que revertir por haber tocado una ruta protegida.
- **Negocio:** las primeras 3 empresas interesadas en servicios o soporte en los 12 meses siguientes.

## Alcance de la v1

**Incluido:**
- Disparadores: GitHub Issues y Sentry, más un comando directo y *polling*. Webhooks para quien lo despliegue en un servidor.
- Cadena Analista → Programador (orquestando un agente existente) → 2–3 revisores.
- **Siempre PR con aprobación humana.** El auto-merge por política llega después y se activa por repositorio.
- Panel React en vivo y `codenetix.yml` por repositorio.
- Distribución con Docker y como GitHub Action.

**Fuera de la v1:** Jira (v1.1), Linear (aparcado: su público son startups, no equipos .NET empresariales), Azure DevOps y GitLab, fallos de CI/CD como disparador, optimización de pipelines, edición Enterprise, Postgres, SSO.

## Equipo y forma de trabajo

Al principio, **un solo desarrollador (Longu) apoyado por los agentes BMAD**: analista, PM, arquitecto, UX y desarrollo. El seguimiento de épicas e historias se lleva en el propio repositorio con el estado de sprint de BMAD, sin herramientas externas. Cuando el proyecto sea público, las tareas abiertas a la comunidad pasarán a GitHub Issues.

## Modelo de sostenibilidad

Open source desde el día uno. Después, por este orden: **servicios** (implantación y adaptación) → **soporte** → **edición Enterprise** *open core*, que vende gobierno (SSO, auditoría, políticas centralizadas, Postgres y alta disponibilidad, Azure DevOps y GitLab). La versión Community nunca tendrá login.

## Visión

En 2–3 años, CodeNetix será **la capa de gobierno estándar para agentes de código autoalojados**, con un ecosistema de revisores y políticas compartidas por la comunidad. Cualquier evento (un error, una tarea, un build roto, una optimización de pipeline) entrará por un lado y saldrá por el otro como código revisado, con el humano solo donde aporta valor (*human in the loop*). El concepto de fondo es el *harness engineering*: diseñar el entorno que hace fiables a los agentes.

## Preguntas abiertas

- **Licencia y CLA:** AGPL, FSL o Apache con carpeta Enterprise comercial. Hay que decidirlo antes del primer commit público y validarlo con un abogado. Registrar la marca "CodeNetix".
- **OpenHands:** ¿tiene o prepara revisores múltiples o merge por políticas? Es el riesgo competitivo número uno.
- **Inspiración:** dar crédito público a Eric Risco y contactar con él.
