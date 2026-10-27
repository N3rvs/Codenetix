# Trabajo aplazado

- source_spec: `_bmad-output/implementation-artifacts/spec-1-1-arrancar-codenetix-con-un-comando.md`
  summary: El comando de ejemplo `docker exec codenetix codenetix fix <url>` (AD-12, bienvenida del Panel) no funciona con el `docker run` documentado en FR-1 y la historia 1.1, que no tiene `--name codenetix`.
  evidence: Docker asigna un nombre aleatorio al contenedor y `docker exec codenetix` falla. Hay que alinear epics, arquitectura y README (añadir `--name codenetix` al comando canónico) como mucho en la historia 1.4, que introduce `codenetix fix`. Severidad medium.
