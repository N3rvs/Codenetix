# Política de seguridad

## Cómo reportar una vulnerabilidad

**Nunca abras una issue pública ni un Pull Request** para reportar una vulnerabilidad de seguridad.

Reporta cualquier hallazgo de forma privada a través de **GitHub Security Advisories**:

- [Abrir un aviso de seguridad privado](https://github.com/N3rvs/codenetix/security/advisories/new) (`https://github.com/N3rvs/codenetix/security/advisories/new`)

### Qué incluir en el reporte

Para poder reproducir y evaluar el problema con rapidez, incluye en la medida de lo posible:

- **Versión afectada:** etiqueta semver (`vX.Y.Z`) o digest `sha256` de la imagen/binario.
- **Pasos de reproducción:** secuencia mínima, configuración relevante o prueba de concepto (PoC).
- **Impacto estimado:** qué frontera de seguridad se ve comprometida (p. ej. fuga de credenciales, escape del Sandbox, omisión de autorización o bypass del Egress Gateway).
- **Mitigación sugerida (opcional):** si conoces una medida temporal o corrección recomendada.

### Plazos orientativos de respuesta

- **Acuse de recibo:** en **≤ 72 horas**.
- **Evaluación inicial y plan de remediación:** en **≤ 7 días**, acordando contigo los tiempos de divulgación coordinada antes de publicar cualquier detalle.

## Versiones soportadas

Solo la última versión semver publicada (`vX.Y.Z`) recibe parches de seguridad. Recomendamos actualizar siempre a la última etiqueta estable verificada.

| Versión | Soportada |
| --- | --- |
| Última `vX.Y.Z` publicada | Sí |
| Versiones anteriores (`< última vX.Y.Z`) | No |
| Commits sin etiquetar (`main` / ramas) | No |

## Modelo de seguridad y responsabilidades del operador

CodeNetix está diseñado con defensa en profundidad, pero su despliegue seguro depende de que el operador entienda y respete sus fronteras de confianza:

- **Riesgo del socket de Docker (`/var/run/docker.sock`):** montar el socket del motor Docker otorga permisos equivalentes a `root` sobre el host Docker. CodeNetix solo utiliza el socket para crear, supervisar y destruir sus propios contenedores Sandbox (etiquetados `codenetix.managed=true`), pero el operador debe ejecutar CodeNetix en un host o VM dedicado y restringir el acceso al contenedor del host.
- **Aislamiento del Sandbox:** cada Ejecución corre en un contenedor desechable conectado únicamente a la red Docker `codenetix-sandbox` (`internal: true`, sin salida directa a internet), con volumen de trabajo sin directorio `.git` ni secretos, sistema de archivos raíz de solo lectura, `/tmp` en memoria, sin capacidades Linux (`--cap-drop=ALL`), `no-new-privileges`, usuario no root `1000:1000` y límites de CPU, memoria y PIDs. Al finalizar o cancelarse la Ejecución, el contenedor y su volumen de trabajo se destruyen.
- **Egress Gateway:** el Sandbox no tiene acceso directo a internet; solo puede comunicarse con `gateway`:
  - `:8080/llm` — proxy inverso hacia el Proveedor de modelos configurado, que sustituye el token efímero `cnx-run-<runId>-<nonce>` por la credencial real y contabiliza consumo y presupuesto.
  - `:8081` — proxy TCP `CONNECT` restringido a la allowlist de NuGet (`api.nuget.org:443` por defecto); cualquier otro destino se bloquea con `403` y registra `EgressBlocked`.
- **Manejo de secretos BYOK y `admin-token`:** las claves de Proveedor (Anthropic, OpenAI u otro proveedor compatible) y `CODENETIX__GitHub__Token` nunca entran al Sandbox ni se escriben en logs (`ISecretRedactor`). El acceso al Panel web y a `/api` requiere el `admin-token` (generado en `/data/admin.token` con permisos `0600` o definido en `CODENETIX__Host__AccessToken`). Mantén el puerto `:5000` enlazado a `127.0.0.1` salvo que coloques TLS y control de acceso delante.
- **Verificación de firmas `cosign` y SBOM:** las imágenes oficiales en `ghcr.io/n3rvs/codenetix` y `ghcr.io/n3rvs/codenetix-sandbox` se publican con etiquetas semver (`vX.Y.Z` y `vX.Y`, nunca `:latest`), firmadas con `cosign` mediante OIDC de GitHub Actions y acompañadas de SBOM y *provenance*. Verifica siempre la firma y el SBOM antes de desplegar (consulta las instrucciones en [README.md](README.md)).
