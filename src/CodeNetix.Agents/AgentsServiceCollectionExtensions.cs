using CodeNetix.Agents.ChatClients;
using CodeNetix.Agents.Features.AnalyzeIssue;
using CodeNetix.Agents.Features.ImplementSpec;
using CodeNetix.Agents.StructuredOutput;
using CodeNetix.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodeNetix.Agents;

public static class AgentsServiceCollectionExtensions
{
    /// <summary>
    /// Registra Agents: <see cref="IAnalyst"/>, el decorador común de reintentos de esquema y la fábrica de
    /// <see cref="Microsoft.Extensions.AI.IChatClient"/> compatible con OpenAI (<see cref="IAgentChatClientFactory"/>).
    /// Necesita <see cref="IRunEventSink"/> (Pipeline). Las <see cref="AgentsOptions"/> las resuelve Host.
    /// </summary>
    public static IServiceCollection AddCodeNetixAgents(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<AgentsOptions>();
        services.TryAddSingleton<IAgentChatClientFactory, OpenAICompatibleChatClientFactory>();
        services.TryAddSingleton<SchemaRetryDecorator>();
        services.TryAddSingleton<IAnalyst, Analyst>();
        services.TryAddSingleton<ICodingAgent, ClaudeCodeAgent>();
        return services;
    }
}
