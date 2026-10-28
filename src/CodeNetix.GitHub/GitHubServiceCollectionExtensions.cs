using CodeNetix.Contracts;
using CodeNetix.GitHub.Data;
using CodeNetix.GitHub.Features.NotifyIssue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CodeNetix.GitHub;

public static class GitHubServiceCollectionExtensions
{
    /// <summary>
    /// Registra el módulo GitHub: <see cref="IGitHubIdentity"/>, <see cref="IRepositoryPermissions"/>,
    /// <see cref="IRepositoryWorkspace"/>, <see cref="IIssueReader"/> e <see cref="IIssueNotifier"/> (AD-8: ningún otro
    /// módulo llama a la API ni clona), más su <c>DbContext</c> y su <see cref="IModuleDatabase"/> (<c>gh</c>). Necesita
    /// <see cref="IDatabaseConnection"/> (Persistence) e <see cref="ISecretRedactor"/> (Host). Las
    /// <see cref="GitHubOptions"/> las enlaza Host (incluido <see cref="GitHubOptions.WorkspacesDirectory"/>).
    /// </summary>
    public static IServiceCollection AddCodeNetixGitHub(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContextFactory<GitHubDbContext>((sp, options) =>
            GitHubDbContext.Configure(options, sp.GetRequiredService<IDatabaseConnection>().ConnectionString));

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IModuleDatabase, GitHubModuleDatabase>();
        services.AddOptions<GitHubOptions>();
        services.TryAddSingleton<GitHubApiClient>();
        services.TryAddSingleton<IGitHubIdentity, GitHubIdentity>();
        services.TryAddSingleton<IRepositoryPermissions, RepositoryPermissions>();
        services.TryAddSingleton<IRepositoryWorkspace, RepositoryWorkspace>();
        services.TryAddSingleton<IIssueReader, IssueReader>();
        services.TryAddSingleton<IIssueNotifier, IssueNotifier>();
        return services;
    }
}
