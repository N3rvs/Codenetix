using CodeNetix.Agents.StructuredOutput;
using CodeNetix.Contracts;

namespace CodeNetix.Agents.Features.ReviewDiff;

/// <summary>Validador del <see cref="Verdict"/> contra <c>schemas/verdict.v1.json</c> y <see cref="VerdictRules"/>.</summary>
internal static class VerdictValidator
{
    public static readonly StructuredOutputValidator<Verdict> Validator =
        new(VerdictSchema.Json, VerdictSchema.Name, VerdictRules.Validate);
}
