namespace CodeNetix.GitHub;

/// <summary>
/// Opciones del módulo GitHub. Host las enlaza desde <c>CODENETIX:GitHub</c> (solo Host lee el entorno).
/// </summary>
public sealed class GitHubOptions
{
    public const string DefaultApiUrl = "https://api.github.com";

    public const string DefaultCloneBaseUrl = "https://github.com";

    /// <summary><c>CODENETIX__GitHub__Token</c>. Nunca se registra ni aparece en excepciones.</summary>
    public string? Token { get; set; }

    /// <summary>
    /// <c>CODENETIX__GitHub__Login</c>: login del bot dueño de <see cref="Token"/>. Si tiene valor, la identidad no llama a
    /// <c>GET /user</c> (un <c>GITHUB_TOKEN</c> de Actions no puede; <c>codenetix action</c> usa <c>github-actions[bot]</c>).
    /// </summary>
    public string? Login { get; set; }

    /// <summary><c>CODENETIX__GitHub__ApiUrl</c>: API de GitHub (o de GitHub Enterprise, p. ej. <c>https://ghe.example.com/api/v3</c>).</summary>
    public string ApiUrl { get; set; } = DefaultApiUrl;

    /// <summary>
    /// <c>CODENETIX__GitHub__CloneBaseUrl</c>: base de las URL de clonado (<c>&lt;base&gt;/&lt;owner&gt;/&lt;repo&gt;.git</c>).
    /// Por defecto <c>https://github.com</c>; admite <c>file://</c> o una ruta local (pruebas) y no admite credenciales en la URL.
    /// </summary>
    public string CloneBaseUrl { get; set; } = DefaultCloneBaseUrl;

    /// <summary>
    /// Directorio de los árboles de trabajo (<c>&lt;DataDirectory&gt;/workspaces</c>). Lo fija Host a partir de
    /// <c>CODENETIX__Persistence__DataDirectory</c>.
    /// </summary>
    public string? WorkspacesDirectory { get; set; }
}
