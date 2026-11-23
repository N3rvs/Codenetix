using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace CodeNetix.Agents.ChatClients;

/// <summary>Crea el <see cref="IChatClient"/> de un rol. Las pruebas lo sustituyen por un cliente falso.</summary>
public interface IAgentChatClientFactory
{
    IChatClient Create(AgentEndpoint endpoint, string model);
}

/// <summary>
/// Todos los Proveedores se tratan como compatibles con OpenAI (<c>Microsoft.Extensions.AI.OpenAI</c>): la key
/// viaja solo en <c>Authorization: Bearer</c>. El SDK no reintenta por su cuenta (los errores HTTP son
/// <c>ProviderError</c> y no consumen reintentos de esquema) y su timeout de red queda por encima del
/// <see cref="AgentsOptions.CallTimeout"/>, que aplica el decorador de reintentos.
/// </summary>
internal sealed class OpenAICompatibleChatClientFactory(IOptions<AgentsOptions> options) : IAgentChatClientFactory
{
    public IChatClient Create(AgentEndpoint endpoint, string model)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = endpoint.BaseUrl,
            NetworkTimeout = options.Value.CallTimeout + TimeSpan.FromSeconds(5),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };

        return new ChatClient(model, new ApiKeyCredential(endpoint.ApiKey), clientOptions).AsIChatClient();
    }
}
