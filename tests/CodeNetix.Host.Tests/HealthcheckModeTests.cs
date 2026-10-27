using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CodeNetix.Host.Tests;

public sealed class HealthcheckModeTests
{
    [Theory]
    [InlineData(200, 0)]
    [InlineData(503, 1)]
    public async Task Maps_health_status_to_exit_code(int statusCode, int expectedExitCode)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = RespondOnceAsync(listener, statusCode, TestContext.Current.CancellationToken);

        var exitCode = await HealthcheckMode.RunAsync(new Uri($"http://127.0.0.1:{port}/api/health"));

        await server;
        Assert.Equal(expectedExitCode, exitCode);
    }

    [Fact]
    public async Task Unreachable_port_exits_with_1()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // puerto cerrado: conexión rechazada

        var exitCode = await HealthcheckMode.RunAsync(new Uri($"http://127.0.0.1:{port}/api/health"));

        Assert.Equal(CodeNetixCommandLine.ExitFailure, exitCode);
    }

    /// <summary>Servidor HTTP mínimo: acepta una conexión, lee la petición y responde con el estado dado.</summary>
    private static async Task RespondOnceAsync(TcpListener listener, int statusCode, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();

        var buffer = new byte[4096];
        var request = new StringBuilder();
        while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        var response = $"HTTP/1.1 {statusCode} X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
    }
}
