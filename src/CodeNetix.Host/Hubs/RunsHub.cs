using Microsoft.AspNetCore.SignalR;

namespace CodeNetix.Host.Hubs;

/// <summary>
/// Hub SignalR en vivo para observar una Ejecución (<c>/hubs/runs</c>, FR-20, AD-3).
/// Autenticado por <see cref="Access.AdminTokenMiddleware"/> (cabecera <c>Bearer</c> o <c>?access_token=</c>).
/// </summary>
internal sealed class RunsHub : Hub
{
    public const string Route = "/hubs/runs";

    /// <summary>Nombre del mensaje enviado a los clientes suscritos con cada <see cref="Contracts.RunEvent"/>.</summary>
    public const string EventMethod = "RunEvent";

    public static string GroupName(Guid runId) => $"run:{runId:D}";

    /// <summary>Suscribe la conexión actual a los eventos de <paramref name="runId"/>.</summary>
    public Task SubscribeRun(Guid runId) =>
        Groups.AddToGroupAsync(Context.ConnectionId, GroupName(runId), Context.ConnectionAborted);

    /// <summary>Cancela la suscripción de la conexión actual a los eventos de <paramref name="runId"/>.</summary>
    public Task UnsubscribeRun(Guid runId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(runId), Context.ConnectionAborted);
}
