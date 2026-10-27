using CodeNetix.Host.Features.GetHealth;
using CodeNetix.Persistence;

namespace CodeNetix.Host;

/// <summary><c>codenetix serve</c>: host completo en <c>:5000</c>.</summary>
internal static class ServeMode
{
    public const string ListenUrl = "http://+:5000";

    public static async Task<int> RunAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(ListenUrl);

        // Solo Host lee el entorno: CODENETIX__Persistence__DataDirectory -> CODENETIX:Persistence.
        builder.Services.AddCodeNetixPersistence();
        builder.Services.Configure<PersistenceOptions>(builder.Configuration.GetSection("CODENETIX:Persistence"));

        builder.Services.AddSingleton<StartupState>();
        builder.Services.AddHostedService<StartupReconciler>();
        builder.Services.AddProblemDetails();

        var app = builder.Build();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGetHealth();

        // /api desconocido -> 404 (ProblemDetails), nunca index.html.
        app.Map("/api/{**rest}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Recurso no encontrado"));

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
            // solo se asegura un mensaje final y un código de salida != 0.
            await Console.Error.WriteLineAsync($"CodeNetix no ha podido arrancar: {ex.Message}");
            return CodeNetixCommandLine.ExitFailure;
        }
    }
}
