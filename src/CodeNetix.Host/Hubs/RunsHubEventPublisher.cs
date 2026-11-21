using System.Text.Json;
using CodeNetix.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace CodeNetix.Host.Hubs;

/// <summary>
/// Publica cada <see cref="RunEvent"/> confirmado en SQLite al grupo <c>run:&lt;runId&gt;</c> de
/// <see cref="RunsHub"/> (AD-3). Serializa explícitamente desde el tipo base polimórfico <see cref="RunEvent"/>
/// para garantizar el mismo discriminador <c>type</c> y formato camelCase que <c>GET /api/runs/{id}/events</c>.
/// </summary>
internal sealed class RunsHubEventPublisher(IHubContext<RunsHub> hubContext) : IRunEventPublisher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public Task PublishAsync(RunEvent runEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runEvent);

        // SignalR serializa cada argumento con su tipo en tiempo de ejecución; serializar primero como RunEvent
        // conserva el discriminador polimórfico "type".
        var payload = JsonSerializer.SerializeToElement<RunEvent>(runEvent, SerializerOptions);
        return hubContext.Clients
            .Group(RunsHub.GroupName(runEvent.RunId))
            .SendAsync(RunsHub.EventMethod, payload, cancellationToken);
    }
}
