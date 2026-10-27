namespace CodeNetix.Persistence;

/// <summary>
/// Ajustes de Persistence. Los rellena Host desde la sección
/// <c>CODENETIX:Persistence</c> (variable <c>CODENETIX__Persistence__DataDirectory</c>).
/// </summary>
public sealed class PersistenceOptions
{
    public const string DefaultDataDirectory = "/data";

    public const string DatabaseFileName = "codenetix.db";

    public string DataDirectory { get; set; } = DefaultDataDirectory;
}
