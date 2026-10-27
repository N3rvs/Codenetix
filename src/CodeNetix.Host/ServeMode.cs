using CodeNetix.Contracts;
using CodeNetix.Host.Configuration;
using CodeNetix.Host.Features.GetHealth;
using CodeNetix.Host.Secrets;
using CodeNetix.Persistence;
using Microsoft.Extensions.Logging.Console;

namespace CodeNetix.Host;

/// <summary><c>codenetix serve</c>: host completo en <c>:5000</c>.</summary>
internal static class ServeMode
{
    public const string ListenUrl = "http://+:5000";

    public static async Task<int> RunAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(ListenUrl);

        // Solo Host lee el entorno. Configuración de instancia = archivo YAML opcional + CODENETIX__*
        // (el entorno tiene prioridad: el archivo se inserta antes de las variables de entorno).
        // Un archivo inválido lanza InstanceConfigurationException aquí, antes de construir el host.
        builder.Configuration.AddInstanceConfigFile();

        builder.Services.AddCodeNetixPersistence();
        builder.Services.Configure<PersistenceOptions>(builder.Configuration.GetSection("CODENETIX:Persistence"));
        builder.Services.Configure<InstanceOptions>(builder.Configuration.GetSection(InstanceOptions.SectionName));

        // Redacción (AD-13): un único ISecretRedactor, aplicado en el sumidero de logs y en /api.
        builder.Services.AddSingleton<ISecretRedactor>(sp => new SecretRedactor(sp.GetRequiredService<IConfiguration>()));
        builder.Logging.ClearProviders();
        builder.Logging
            .AddConsole(options => options.FormatterName = RedactingConsoleFormatter.FormatterName)
            .AddConsoleFormatter<RedactingConsoleFormatter, ConsoleFormatterOptions>();

        builder.Services.AddSingleton<InstanceHealth>();
        builder.Services.AddSingleton<IInstanceHealth>(sp => sp.GetRequiredService<InstanceHealth>());
        builder.Services.AddHostedService<StartupReconciler>();
        builder.Services.AddProblemDetails();

        var app = builder.Build();

        // Resuelto antes de arrancar: si el arranque falla, el contenedor ya estará liberado.
        var redactor = app.Services.GetRequiredService<ISecretRedactor>();

        app.UseMiddleware<ApiRedactionMiddleware>();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGetHealth();

        // /api desconocido -> 404 (ProblemDetails), nunca index.html.
        app.Map("/api/{**rest}", (HttpContext context) => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Recurso no encontrado",
            instance: context.Request.Path));

        // Rutas del Panel (SPA) -> index.html.
        app.MapFallbackToFile("index.html");

        try
        {
            await app.RunAsync();
            return CodeNetixCommandLine.ExitOk;
        }
        catch (Exception ex)
        {
            // StartupReconciler y el host ya registraron el detalle (el logger puede estar liberado aquí);
            // solo se asegura un mensaje final (redactado: no pasa por el sumidero de logs) y un código != 0.
            await Console.Error.WriteLineAsync(redactor.Redact($"CodeNetix no ha podido arrancar: {ex.Message}"));
            return CodeNetixCommandLine.ExitFailure;
        }
    }
}
