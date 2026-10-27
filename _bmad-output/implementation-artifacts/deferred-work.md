# Trabajo aplazado

- source_spec: `_bmad-output/implementation-artifacts/spec-1-1-arrancar-codenetix-con-un-comando.md`
  summary: El comando de ejemplo `docker exec codenetix codenetix fix <url>` (AD-12, bienvenida del Panel) no funciona con el `docker run` documentado en FR-1 y la historia 1.1, que no tiene `--name codenetix`.
  evidence: Docker asigna un nombre aleatorio al contenedor y `docker exec codenetix` falla. Hay que alinear epics, arquitectura y README (añadir `--name codenetix` al comando canónico) como mucho en la historia 1.4, que introduce `codenetix fix`. Severidad medium.

- source_spec: `_bmad-output/implementation-artifacts/spec-1-2-configurar-la-instancia-y-modo-diagnostico.md`
  summary: La vista de diagnóstico del Panel (motivos en `Diagnostic`, bienvenida en `Ready`) no tiene verificación automática.
  evidence: `panel/` no tiene runner de tests y la historia 1.2 prohibía añadir dependencias de frontend; hoy solo lo cubre el humo manual con Docker. Severidad medium (sin verificar). Se resolvería al introducir Vitest/Testing Library en la Épica 4.
