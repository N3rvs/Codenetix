using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace CodeNetix.Host.Tests;

/// <summary>Host de pruebas con un <c>DataDirectory</c> y un <c>WebRootPath</c> temporales.</summary>
internal sealed class CodeNetixFactory(string dataDirectory, string webRoot) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Fuera de Development: así no se superponen los static web assets del wwwroot real
        // del proyecto (el Panel compilado) sobre el WebRootPath temporal.
        builder.UseEnvironment("Testing");
        builder.UseWebRoot(webRoot);
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CODENETIX:Persistence:DataDirectory"] = dataDirectory,
        }));
    }
}
