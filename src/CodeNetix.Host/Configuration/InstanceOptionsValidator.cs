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

    public const string InvalidPollingRepository =
        "CODENETIX__Polling__Repositories debe ser una lista de repositorios owner/repo separados por comas (o CODENETIX__Polling__Repositories__0, __1…); hay al menos un elemento que no lo es.";

    public const string TooManyPollingRepositories =
        "CODENETIX__Polling__Repositories admite como mucho 100 repositorios.";

    public const string InvalidPollingLabel =
        "CODENETIX__Polling__Label debe ser un nombre de etiqueta de 1 a 50 caracteres, sin comas ni caracteres de control (por defecto codenetix).";

    public const string InvalidPollingInterval =
        "CODENETIX__Polling__IntervalSeconds debe ser un entero entre 60 y 86400 (por defecto 300).";

    public const string InvalidWebhookSecret =
        "CODENETIX__GitHub__WebhookSecret debe tener al menos 16 caracteres y ninguno de control (o no definirse para desactivar el webhook de GitHub).";

    public const string InvalidWebhookRepository =
        "CODENETIX__GitHub__WebhookRepositories debe ser una lista de repositorios owner/repo separados por comas (o CODENETIX__GitHub__WebhookRepositories__0, __1…); hay al menos un elemento que no lo es.";

    public const string TooManyWebhookRepositories =
        "CODENETIX__GitHub__WebhookRepositories admite como mucho 100 repositorios.";

    public const string InvalidSentryWebhookSecret =
        "CODENETIX__Sentry__WebhookSecret debe tener al menos 16 caracteres y ninguno de control (o no definirse para desactivar el webhook de Sentry).";

    public const string InvalidSentryProjectSlug =
        "CODENETIX__Sentry__Projects contiene un proyecto cuyo nombre no es un slug de Sentry válido (letras, dígitos, '-' y '_', hasta 100 caracteres).";

    public const string TooManySentryProjects =
        "CODENETIX__Sentry__Projects admite como mucho 100 proyectos.";

    public const int MinSentryAuthTokenLength = 8;

    public const string InvalidSentryAuthToken =
        "CODENETIX__Sentry__AuthToken debe tener al menos 8 caracteres y ninguno de control (o no definirse para desactivar el contexto de Sentry).";

    public const string MissingSentryOrganization =
        "CODENETIX__Sentry__Organization es obligatorio cuando se define CODENETIX__Sentry__AuthToken (el slug de la organización de Sentry).";

    public const string InvalidSentryOrganization =
        "CODENETIX__Sentry__Organization no es un slug de organización de Sentry válido (letras, dígitos, '-' y '_', hasta 100 caracteres).";

    public const string InvalidSentryBaseUrl =
        "CODENETIX__Sentry__BaseUrl debe ser una URL http(s) absoluta sin credenciales, query ni fragmento (por defecto https://sentry.io).";

    public static string InvalidSentryProjectRepository(string slug) =>
        $"CODENETIX__Sentry__Projects__{slug} debe ser un repositorio owner/repo.";

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

        // Proveedor, URL y modelo del Analista, del Programador y de los Revisores (solo si hay alguna key: si no, ya falta la key).
        var analystReasons = AgentEndpointResolver.ResolveAnalyst(options).Reasons;
        reasons.AddRange(analystReasons);
        if (analystReasons.Count == 0
            || !string.IsNullOrWhiteSpace(options.Agents?.Coder?.Provider)
            || !string.IsNullOrWhiteSpace(options.Agents?.Coder?.Model))
        {
            AddDistinct(reasons, AgentEndpointResolver.ResolveCoder(options).Reasons);
        }

        if (!string.IsNullOrWhiteSpace(options.Agents?.Reviewers?.Provider)
            || !string.IsNullOrWhiteSpace(options.Agents?.Reviewers?.Model))
        {
            AddDistinct(reasons, AgentEndpointResolver.ResolveReviewers(options).Reasons);
        }

        foreach (var kind in Enum.GetValues<CodeNetix.Contracts.ReviewerKind>())
        {
            AddDistinct(reasons, AgentEndpointResolver.ResolveReviewer(options, kind).Reasons);
        }

        if (options.Pricing is { Count: > 0 })
        {
            foreach (var (modelName, pricing) in options.Pricing)
            {
                if (string.IsNullOrWhiteSpace(modelName))
                {
                    reasons.Add("CODENETIX__Pricing contiene una entrada con nombre de modelo vacío.");
                    continue;
                }

                if (pricing is null)
                {
                    reasons.Add($"CODENETIX__Pricing__{modelName} debe definir InputPerMillionUsd y OutputPerMillionUsd mayores o iguales que 0.");
                    continue;
                }

                if (!pricing.InputPerMillionUsd.HasValue || pricing.InputPerMillionUsd.Value < 0)
                {
                    reasons.Add($"CODENETIX__Pricing__{modelName}__InputPerMillionUsd es obligatorio y debe ser mayor o igual que 0.");
                }

                if (!pricing.OutputPerMillionUsd.HasValue || pricing.OutputPerMillionUsd.Value < 0)
                {
                    reasons.Add($"CODENETIX__Pricing__{modelName}__OutputPerMillionUsd es obligatorio y debe ser mayor o igual que 0.");
                }
            }
        }

        ValidatePolling(options.Polling ?? new PollingInstanceOptions(), reasons);
        ValidateWebhook(options.GitHub ?? new GitHubInstanceOptions(), reasons);
        ValidateSentry(options.Sentry ?? new SentryInstanceOptions(), reasons);

        return reasons;
    }

    private static void ValidateSentry(SentryInstanceOptions sentry, List<string> reasons)
    {
        if (sentry.EffectiveAuthToken is { } authToken && (authToken.Length < MinSentryAuthTokenLength || authToken.Any(char.IsControl)))
        {
            reasons.Add(InvalidSentryAuthToken);
        }

        if (sentry.EffectiveOrganization is { } organization)
        {
            if (!CodeNetix.Ingestion.Features.ReadSentryContext.SentryApiOptions.IsOrganizationSlug(organization))
            {
                reasons.Add(InvalidSentryOrganization);
            }
        }
        else if (sentry.EffectiveAuthToken is not null)
        {
            reasons.Add(MissingSentryOrganization);
        }

        if (!CodeNetix.Ingestion.Features.ReadSentryContext.SentryApiOptions.IsValidBaseUrl(sentry.EffectiveBaseUrl))
        {
            reasons.Add(InvalidSentryBaseUrl);
        }

        if (sentry.EffectiveWebhookSecret is { } secret
            && (secret.Length < GitHubInstanceOptions.MinWebhookSecretLength || secret.Any(char.IsControl)))
        {
            reasons.Add(InvalidSentryWebhookSecret);
        }

        var projects = sentry.Projects ?? [];
        if (projects.Count > CodeNetix.Ingestion.Features.ReceiveSentryWebhook.SentryWebhookOptions.MaxProjects)
        {
            reasons.Add(TooManySentryProjects);
        }

        var invalidSlug = false;
        foreach (var (key, value) in projects.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var slug = key.Trim().ToLowerInvariant();
            if (!CodeNetix.Ingestion.Features.ReceiveSentryWebhook.SentryWebhookOptions.IsProjectSlug(slug))
            {
                // El nombre no se cita: puede ser cualquier texto.
                invalidSlug = true;
                continue;
            }

            if (!PollingInstanceOptions.TryParseRepository(value, out _, out _))
            {
                reasons.Add(InvalidSentryProjectRepository(slug));
            }
        }

        if (invalidSlug)
        {
            reasons.Add(InvalidSentryProjectSlug);
        }
    }

    private static void ValidateWebhook(GitHubInstanceOptions gitHub, List<string> reasons)
    {
        if (gitHub.EffectiveWebhookSecret is { } secret
            && (secret.Length < GitHubInstanceOptions.MinWebhookSecretLength || secret.Any(char.IsControl)))
        {
            reasons.Add(InvalidWebhookSecret);
        }

        ValidateRepositories(gitHub.WebhookRepositories, InvalidWebhookRepository, TooManyWebhookRepositories, reasons);
    }

    private static void ValidateRepositories(List<string>? repositories, string invalid, string tooMany, List<string> reasons)
    {
        repositories ??= [];
        if (repositories.Any(r => !PollingInstanceOptions.TryParseRepository(r, out _, out _)))
        {
            reasons.Add(invalid);
        }

        if (repositories.Select(r => r.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).Count() > PollingInstanceOptions.MaxRepositories)
        {
            reasons.Add(tooMany);
        }
    }

    private static void ValidatePolling(PollingInstanceOptions polling, List<string> reasons)
    {
        ValidateRepositories(polling.Repositories, InvalidPollingRepository, TooManyPollingRepositories, reasons);

        if (polling.Label is { } label && !string.IsNullOrWhiteSpace(label))
        {
            var trimmed = label.Trim();
            if (trimmed.Length > PollingInstanceOptions.MaxLabelLength || trimmed.Contains(',') || trimmed.Any(char.IsControl))
            {
                reasons.Add(InvalidPollingLabel);
            }
        }

        if (polling.IntervalSeconds is < PollingInstanceOptions.MinIntervalSeconds or > PollingInstanceOptions.MaxIntervalSeconds)
        {
            reasons.Add(InvalidPollingInterval);
        }
    }

    private static void AddDistinct(List<string> target, IReadOnlyList<string> items)
    {
        foreach (var item in items)
        {
            if (!target.Contains(item, StringComparer.Ordinal))
            {
                target.Add(item);
            }
        }
    }
}
