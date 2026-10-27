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
          serve         Arranca CodeNetix (Panel y API en http://localhost:5000).
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
            "serve" => await ServeMode.RunAsync(args[1..]),
            "healthcheck" => await HealthcheckMode.RunAsync(),
            _ => WriteUsage(mode),
        };
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
