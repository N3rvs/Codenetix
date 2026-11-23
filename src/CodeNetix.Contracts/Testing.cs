namespace CodeNetix.Contracts;

/// <summary>
/// Línea base de tests existentes en el repositorio antes de que el Programador realice cambios (AD-6, FR-12).
/// </summary>
/// <param name="ExistingTests">Conjunto de nombres de tests detectados en la ejecución base.</param>
public sealed record TestBaseline(IReadOnlySet<string> ExistingTests)
{
    public static TestBaseline Empty { get; } = new(new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>Resultado individual de un caso de prueba extraído del TRX.</summary>
/// <param name="Name">Nombre completamente cualificado del test.</param>
/// <param name="Outcome">Resultado (<c>Passed</c>, <c>Failed</c>, <c>NotExecuted</c>, …).</param>
/// <param name="ErrorMessage">Mensaje de error del fallo, si lo hay.</param>
/// <param name="StackTrace">Traza de pila del fallo, si la hay.</param>
public sealed record TestCaseResult(
    string Name,
    string Outcome,
    string? ErrorMessage = null,
    string? StackTrace = null);

/// <summary>
/// Informe estructurado de compilación y tests producido por <see cref="ITestRunner"/> (AD-6, FR-12).
/// La decisión de aprobación siempre deriva de <see cref="Passed"/> y nunca de afirmaciones del agente.
/// </summary>
public sealed record TestReport(
    bool BuildSucceeded,
    int TotalTests,
    int PassedTests,
    int FailedTests,
    int SkippedTests,
    IReadOnlyList<string> NewTests,
    IReadOnlyList<TestCaseResult> FailedTestCases,
    TimeSpan Duration,
    string? BuildOutput = null)
{
    /// <summary>
    /// <c>true</c> solo si la compilación tuvo éxito, no falló ningún test y existe al menos un test nuevo respecto a la línea base.
    /// </summary>
    public bool Passed => BuildSucceeded && FailedTests == 0 && NewTests.Count > 0;
}

/// <summary>
/// Ejecutor de compilación y tests dentro del Sandbox (AD-1, AD-6). Solo lo implementa el módulo Sandbox.
/// </summary>
public interface ITestRunner
{
    /// <summary>
    /// Captura la línea base de tests existentes en <paramref name="session"/> antes del bucle de programación.
    /// Lanza <see cref="SandboxUnavailableException"/> si el Sandbox falla o el comando agota su timeout.
    /// </summary>
    Task<TestBaseline> CaptureBaselineAsync(ISandboxSession session, CancellationToken cancellationToken);

    /// <summary>
    /// Ejecuta <c>dotnet build</c> y <c>dotnet test</c> dentro de <paramref name="session"/> y calcula el informe
    /// diferencial respecto a <paramref name="baseline"/>. Lanza <see cref="SandboxUnavailableException"/> si el
    /// Sandbox falla o algún comando agota su timeout.
    /// </summary>
    Task<TestReport> RunAsync(ISandboxSession session, TestBaseline baseline, CancellationToken cancellationToken);
}
