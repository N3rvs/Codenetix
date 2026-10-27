namespace CodeNetix.Contracts;

/// <summary>
/// Único enmascarador de secretos de la instancia (AD-13). Se aplica en los sumideros
/// (logs, respuestas de <c>/api</c>, eventos de Ejecución); los productores no redactan.
/// </summary>
public interface ISecretRedactor
{
    /// <summary>Devuelve <paramref name="value"/> con todos los secretos conocidos y patrones de keys enmascarados.</summary>
    string Redact(string value);
}
