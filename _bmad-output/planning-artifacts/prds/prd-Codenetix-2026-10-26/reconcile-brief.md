# Reconciliación: brief + addendum + research → PRD

| Elemento de las fuentes | ¿Recogido en el PRD? | Dónde / acción |
|---|---|---|
| Cero fricción, sin login ni base de datos | Sí | FR-1, FR-2, §7 |
| Coste cero para el mantenedor, BYOK, sin telemetría | Sí | NFR-2, NFR-3 |
| La política decide, no el LLM | Sí | FR-18, NFR-6, §7 |
| El código no sale de la infraestructura; **admite modelos locales** (principio 4 del brief) | **Parcial** | NFR-2 cubre la privacidad, pero no hay ningún requisito sobre modelos locales. El Programador de la v1 es Claude Code, así que los modelos locales solo serían viables para los Revisores → **GAP** |
| v1 siempre PR; auto-merge después | Sí | FR-17, §8.2 |
| GitHub + Sentry, comando directo, sondeo, webhooks | Sí | FR-5 a FR-8 |
| Jira en v1.1, Linear aparcado | Sí | §8.2 |
| Panel React en vivo | Sí (sin tecnología, correcto) | FR-20 a FR-23 |
| Distribución con Docker + GitHub Action | Sí | FR-1, FR-25 |
| Criterios de éxito del brief | Sí, salvo "ningún merge automático revertido" | No aplica en la v1 (no hay auto-merge) → set aside |
| Modelo de negocio / open core | Fuera del PRD (correcto) | Está en el brief |
| Crédito a Eric Risco, licencia, OpenHands | Sí | §10 |
| Objetivo de visibilidad o marca personal (frase cortada del usuario) | No | Sin confirmar → set aside |
| Tono: transparencia y confianza ("la cadena de montaje visible") | Sí | §1, §4.7 |
| Matiz: el tipado estricto no evita las alucinaciones | Sí, implícito | FR-9 y FR-15 hablan de esquemas, no de "corrección" |
