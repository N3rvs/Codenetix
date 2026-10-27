# Contribuir a CodeNetix

¡Gracias por tu interés en contribuir! CodeNetix se publica bajo la [licencia Apache 2.0](LICENSE).

## Developer Certificate of Origin (DCO)

No pedimos firmar ningún CLA. En su lugar usamos el [Developer Certificate of Origin](https://developercertificate.org/): al contribuir, certificas que tienes derecho a enviar ese código bajo la licencia del proyecto.

Para aceptarlo, **firma cada commit** con la opción `-s`:

```bash
git commit -s -m "Describe tu cambio"
```

Esto añade al mensaje del commit una línea como esta:

```
Signed-off-by: Tu Nombre <tu@email.com>
```

Los PR con commits sin firmar no se pueden fusionar. Si olvidaste firmar, puedes corregirlo con:

```bash
git commit --amend -s --no-edit        # último commit
git rebase --signoff main              # varios commits
```

## Seguridad

No abras issues públicas para reportar vulnerabilidades. Consulta `SECURITY.md`.
