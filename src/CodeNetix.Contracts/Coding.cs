namespace CodeNetix.Contracts;

/// <summary>Entrada de un intento del Programador (AD-16 paso 4).</summary>
/// <param name="Attempt">Número de intento, empezando en 1.</param>
/// <param name="Feedback">Motivo del fallo del intento anterior (dato no confiable), o <c>null</c> en el primero.</param>
public sealed record CodingInput(
    Guid RunId,
    int Attempt,
    UntrustedText IssueTitle,
    UntrustedText IssueBody,
    Spec Spec,
    EffectiveConfig Config,
    ISandboxSession Sandbox,
    string? Feedback = null);

/// <summary>Resultado de un intento del Programador: <see cref="Completed"/>, <see cref="Failed"/> o <see cref="BudgetExhausted"/>.</summary>
public abstract record CodingResult
{
    private CodingResult()
    {
    }

    /// <summary>El CLI terminó bien y dejó cambios en <c>/workspace</c>.</summary>
    public sealed record Completed(string Summary, string Model) : CodingResult;

    /// <summary>Error, timeout o sin cambios: el intento cuenta como fallido. <paramref name="Reason"/> va en español.</summary>
    public sealed record Failed(string Reason) : CodingResult;

    /// <summary>La Ejecución agotó su presupuesto antes o durante el intento de programación (FR-13, AD-15).</summary>
    public sealed record BudgetExhausted(string Detail) : CodingResult;
}

/// <summary>
/// Programador (FR-11, AD-5): implementa la <see cref="Spec"/> dentro del Sandbox. Emite la salida del proceso como
/// <see cref="OutputAppended"/> en <see cref="IRunEventSink"/>.
/// </summary>
public interface ICodingAgent
{
    Task<CodingResult> RunAsync(CodingInput input, CancellationToken cancellationToken);
}

