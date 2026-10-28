# CodeNetix

CodeNetix convierte una issue de GitHub en un cambio revisado: analiza la issue, programa la corrección dentro de
un Sandbox aislado, la prueba y la revisa antes de proponer un PR. Se publica bajo la [licencia Apache 2.0](LICENSE).

> Estado: en desarrollo. Hoy CodeNetix genera la Spec de la issue (o la rechaza con un aviso en la issue) y prepara
> el Sandbox de cada Ejecución; la programación, las pruebas y el PR llegan en las próximas historias.

## Requisitos

- Docker (Docker Engine 24 o superior, o Docker Desktop) en Linux, macOS o Windows, x64 o arm64.
- Un token de GitHub y la key de al menos un Proveedor de modelos.

## 1. Construir las imágenes

Desde la raíz del repositorio:

```bash
# Imagen de CodeNetix
docker build -f docker/codenetix.Dockerfile -t codenetix .

# Imagen del Sandbox: el contenedor desechable en el que se ejecuta el código de cada Ejecución
docker build -f docker/sandbox.Dockerfile -t codenetix-sandbox .
```

La imagen del Sandbox (`codenetix-sandbox:latest`) trae el SDK de .NET 10 y Node 24 LTS y se ejecuta con el usuario
no root `sandbox`. Si falta, CodeNetix arranca en modo diagnóstico y `/api/health` indica cómo construirla. Para usar
otro nombre, define `CODENETIX__Sandbox__Image`.

## 2. Arrancar CodeNetix

CodeNetix crea un Sandbox por Ejecución a través del motor Docker del host, así que su contenedor necesita el socket
de Docker:

```bash
docker run -d --name codenetix \
  -p 127.0.0.1:5000:5000 \
  -v codenetix-data:/data \
  -v /var/run/docker.sock:/var/run/docker.sock \
  --group-add "$(stat -c '%g' /var/run/docker.sock)" \
  -e CODENETIX__GitHub__Token=ghp_... \
  -e CODENETIX__Providers__Anthropic__ApiKey=sk-ant-... \
  codenetix serve
```

- `-v /var/run/docker.sock:/var/run/docker.sock`: sin el socket, CodeNetix arranca en modo diagnóstico ("Docker no
  responde…").
- `--group-add`: CodeNetix no se ejecuta como root; necesita el grupo propietario del socket para poder usarlo. Con
  Docker Desktop (macOS y Windows) el socket pertenece a root: usa `--group-add 0`.
- **Seguridad:** quien controla el socket de Docker controla el host. CodeNetix solo lo usa para crear y destruir
  sus Sandboxes (etiquetados `codenetix.managed=true`), siempre sin red, sin capacidades, sin *bind mounts* y con
  límites de CPU, memoria y procesos. No expongas el Panel fuera de localhost sin `CODENETIX__Host__AccessToken`.

Comprueba el estado con `curl http://127.0.0.1:5000/api/health` y lanza trabajo con:

```bash
docker exec codenetix codenetix fix https://github.com/<owner>/<repo>/issues/<número> --follow
```

## El Sandbox de cada Ejecución

| Qué | Cómo |
| --- | --- |
| Contenedor | `codenetix-sbx-<shortId>`, etiquetas `codenetix.run=<runId>` y `codenetix.managed=true` |
| Árbol de trabajo | Volumen `codenetix-ws-<runId>` en `/workspace`: solo los archivos versionados del commit base, nunca `.git` ni credenciales |
| Herramientas | Volumen `codenetix-tools` en solo lectura en `/tools` (se conserva entre Ejecuciones) |
| Aislamiento | Sin red (`none`), sistema de archivos raíz de solo lectura, `/tmp` en memoria, sin capacidades, `no-new-privileges`, usuario 1000:1000 |
| Ciclo de vida | Se crea tras clonar el repositorio y se destruye (contenedor y volumen de trabajo) al terminar la Ejecución, en cualquier Estado; al cancelarse se detiene en 10 s como máximo |

Variables (todas opcionales):

| Variable | Por defecto |
| --- | --- |
| `CODENETIX__Sandbox__Image` | `codenetix-sandbox:latest` |
| `CODENETIX__Sandbox__DockerEndpoint` | `DOCKER_HOST` o `unix:///var/run/docker.sock` (`npipe://./pipe/docker_engine` en Windows) |
| `CODENETIX__Sandbox__Cpus` | `2` |
| `CODENETIX__Sandbox__MemoryMb` | `4096` |
| `CODENETIX__Sandbox__PidsLimit` | `512` |
| `CODENETIX__Sandbox__MaxLifetimeMinutes` | `60` |

## Desarrollo

```bash
dotnet build CodeNetix.slnx
dotnet test --solution CodeNetix.slnx
```

Las pruebas de integración del Sandbox usan el Docker local (Docker Desktop en Windows) y la imagen
`codenetix-sandbox`; si Docker no responde o falta la imagen, se omiten. Todo lo que crean lleva la etiqueta
`codenetix.managed=true` y se borra al terminar.

Consulta [CONTRIBUTING.md](CONTRIBUTING.md) para contribuir.
