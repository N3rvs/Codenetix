namespace CodeNetix.Host;

/// <summary>
/// Subcomando interno para el <c>HEALTHCHECK</c> de la imagen: GET a <c>/api/health</c>,
/// sale con 0 si responde 2xx y con 1 en cualquier otro caso. Evita instalar curl.
/// </summary>
internal static class HealthcheckMode
{
    internal static readonly Uri DefaultHealthUri = new("http://127.0.0.1:5000/api/health");

    public static async Task<int> RunAsync(Uri? healthUri = null)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(healthUri ?? DefaultHealthUri);
            return response.IsSuccessStatusCode ? CodeNetixCommandLine.ExitOk : CodeNetixCommandLine.ExitFailure;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"healthcheck: {ex.Message}");
            return CodeNetixCommandLine.ExitFailure;
        }
    }
}
