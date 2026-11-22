namespace CodeNetix.Host;

/// <summary>Despacho de modos del binario <c>codenetix</c> (AD-12).</summary>
internal static class CodeNetixCommandLine
{
    public const int ExitOk = 0;
    public const int ExitFailure = 1;
    public const int ExitUsage = 2;

    internal const string Usage =
        """
        Uso: codenetix <modo>

        Modos:
          serve         Arranca CodeNetix (Panel y API en http://localhost:5000; webhooks en :5001).
          fix <url>     Lanza trabajo sobre una issue de GitHub (https://github.com/<owner>/<repo>/issues/<número>).
                        Se ejecuta dentro del contenedor: docker exec codenetix codenetix fix <url>
                        Con --follow muestra el Estado y los eventos de la Ejecución hasta que termina.
          action        Ejecuta la factoría dentro de un job de GitHub Actions al etiquetar una issue (codenetix),
                        sin Panel y con SQLite temporal; vuelca los eventos de la Ejecución a la salida estándar.
                        Sale con 0 (PR creado o nada que hacer), 1 (Ejecución fallida, no autorizado o error) o 2 (uso).
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : null;

        // Host de pruebas (WebApplicationFactory): invoca el punto de entrada solo con interruptores
        // "--clave=valor", siempre incluido "--applicationName=". Solo ese caso se trata como serve.
        if (IsTestHostInvocation(args))
        {
            return await ServeMode.RunAsync(args);
        }

        return mode switch
        {
            "serve" => await ServeAsync(args[1..]),
            "fix" => await FixMode.RunAsync(args[1..]),
            "action" => await ActionAsync(args[1..]),
            "healthcheck" => await HealthcheckMode.RunAsync(),
            _ => WriteUsage(mode),
        };
    }

    /// <summary>
    /// Cualquier excepción de arranque (p. ej. un archivo de configuración de instancia inválido,
    /// que se detecta antes de construir el host) termina con <see cref="ExitFailure"/>.
    /// </summary>
    private static async Task<int> ServeAsync(string[] args)
    {
        try
        {
            return await ServeMode.RunAsync(args);
        }
        catch (Exception ex)
        {
            // Aún no hay configuración ni sumidero de logs: solo se enmascaran los patrones de keys.
            var message = new Secrets.SecretRedactor([]).Redact($"CodeNetix no ha podido arrancar: {ex.Message}");
            await Console.Error.WriteLineAsync(message);
            return ExitFailure;
        }
    }

    private static async Task<int> ActionAsync(string[] args)
    {
        try
        {
            return await ActionMode.RunAsync(args);
        }
        catch (Exception ex)
        {
            var message = new Secrets.SecretRedactor([]).Redact($"codenetix action ha fallado: {ex.Message}");
            await Console.Error.WriteLineAsync(message);
            return ExitFailure;
        }
    }

    private static bool IsTestHostInvocation(string[] args) =>
        args.Length > 0
        && args.All(a => a.StartsWith("--", StringComparison.Ordinal))
        && args.Any(a => a.StartsWith("--applicationName=", StringComparison.Ordinal));

    private static int WriteUsage(string? mode)
    {
        if (mode is not null)
        {
            Console.Error.WriteLine($"Modo desconocido: '{mode}'.");
            Console.Error.WriteLine();
        }

        Console.Error.WriteLine(Usage);
        return ExitUsage;
    }
}
