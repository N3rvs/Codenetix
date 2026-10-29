namespace CodeNetix.Host.Configuration;

/// <summary>
/// Comprueba lo obligatorio de la Configuración de instancia. Cada motivo nombra la variable
/// que falta y nunca incluye un valor.
/// </summary>
internal static class InstanceOptionsValidator
{
    public const string MissingGitHubToken =
        "Falta el token de GitHub: define la variable CODENETIX__GitHub__Token.";

    public const string MissingProviderApiKey =
        "Falta la key de al menos un Proveedor: define la variable CODENETIX__Providers__<Nombre>__ApiKey (por ejemplo, CODENETIX__Providers__Anthropic__ApiKey).";

    public const string InvalidMaxReviewRounds =
        "CODENETIX__Limits__MaxReviewRounds debe ser un entero mayor o igual que 1.";

    public const string InvalidMaxCodingAttempts =
        "CODENETIX__Limits__MaxCodingAttempts debe ser un entero mayor o igual que 1.";

    public const string InvalidBudgetUsd =
        "CODENETIX__Limits__BudgetUsd debe ser mayor que 0 (o no definirse para no tener límite).";

    public const string InvalidSchemaRetries =
        "CODENETIX__Agents__SchemaRetries debe ser un entero entre 0 y 5.";

    public const string InvalidSandboxImage =
        "CODENETIX__Sandbox__Image no puede estar vacío (por defecto codenetix-sandbox:latest).";

    public const string InvalidSandboxCpus =
        "CODENETIX__Sandbox__Cpus debe ser un número mayor que 0 (por defecto 2).";

    public const string InvalidSandboxMemoryMb =
        "CODENETIX__Sandbox__MemoryMb debe ser un entero mayor o igual que 256 (por defecto 4096).";

    public const string InvalidSandboxPidsLimit =
        "CODENETIX__Sandbox__PidsLimit debe ser un entero mayor o igual que 64 (por defecto 512).";

    public const string InvalidSandboxMaxLifetimeMinutes =
        "CODENETIX__Sandbox__MaxLifetimeMinutes debe ser un entero entre 1 y 1440 (por defecto 60).";

    public static IReadOnlyList<string> Validate(InstanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var reasons = new List<string>();

        if (string.IsNullOrWhiteSpace(options.GitHub?.Token))
        {
            reasons.Add(MissingGitHubToken);
        }

        if (options.Providers is null || !options.Providers.Values.Any(p => !string.IsNullOrWhiteSpace(p?.ApiKey)))
        {
            reasons.Add(MissingProviderApiKey);
        }

        var limits = options.Limits ?? new LimitsInstanceOptions();
        if (limits.MaxReviewRounds < 1)
        {
            reasons.Add(InvalidMaxReviewRounds);
        }

        if (limits.MaxCodingAttempts < 1)
        {
            reasons.Add(InvalidMaxCodingAttempts);
        }

        if (limits.BudgetUsd is <= 0)
        {
            reasons.Add(InvalidBudgetUsd);
        }

        var agents = options.Agents ?? new AgentsInstanceOptions();
        if (agents.SchemaRetries is < 0 or > CodeNetix.Agents.AgentsOptions.MaxSchemaRetries)
        {
            reasons.Add(InvalidSchemaRetries);
        }

        var sandbox = options.Sandbox ?? new CodeNetix.Sandbox.SandboxOptions();
        if (string.IsNullOrWhiteSpace(sandbox.Image))
        {
            reasons.Add(InvalidSandboxImage);
        }

        if (!(sandbox.Cpus > 0) || double.IsInfinity(sandbox.Cpus))
        {
            reasons.Add(InvalidSandboxCpus);
        }

        if (sandbox.MemoryMb < 256)
        {
            reasons.Add(InvalidSandboxMemoryMb);
        }

        if (sandbox.PidsLimit < 64)
        {
            reasons.Add(InvalidSandboxPidsLimit);
        }

        if (sandbox.MaxLifetimeMinutes is < 1 or > CodeNetix.Sandbox.SandboxOptions.MaxLifetimeMinutesLimit)
        {
            reasons.Add(InvalidSandboxMaxLifetimeMinutes);
        }

        // Proveedor, URL y modelo del Analista y del Programador (solo si hay alguna key: si no, ya falta la key).
        var analystReasons = AgentEndpointResolver.ResolveAnalyst(options).Reasons;
        reasons.AddRange(analystReasons);
        if (analystReasons.Count == 0
            || !string.IsNullOrWhiteSpace(options.Agents?.Coder?.Provider)
            || !string.IsNullOrWhiteSpace(options.Agents?.Coder?.Model))
        {
            foreach (var coderReason in AgentEndpointResolver.ResolveCoder(options).Reasons)
            {
                if (!reasons.Contains(coderReason, StringComparer.Ordinal))
                {
                    reasons.Add(coderReason);
                }
            }
        }

        return reasons;
    }
}
