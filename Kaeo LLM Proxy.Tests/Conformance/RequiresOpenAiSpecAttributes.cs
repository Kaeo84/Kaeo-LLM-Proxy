using Xunit;

namespace Kaeo.LlmProxy.Tests.Conformance;

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped when the vendored OpenAI specification is not on
/// disk.
/// </summary>
/// <remarks>
/// <c>API Specs/</c> is listed in <c>.gitignore</c>, so the vendor documents are local reference
/// content rather than something every clone contains. Tests that validate against a spec must
/// therefore skip on a clone that lacks it instead of failing, otherwise the suite would be red for
/// anyone who has not populated the folder. When the spec is present these run normally, and a
/// genuinely non-conformant frame still fails.
/// </remarks>
internal sealed class RequiresOpenAiSpecFactAttribute : FactAttribute
{
    public RequiresOpenAiSpecFactAttribute()
    {
        if (!SpecCatalog.HasOpenAiSpec)
        {
            Skip = "The vendored OpenAI specification (API Specs/openai-openapi) is not present; "
                + "it is gitignored reference content, so this conformance test is skipped.";
        }
    }
}

/// <summary>
/// The <see cref="TheoryAttribute"/> counterpart of <see cref="RequiresOpenAiSpecFactAttribute"/>,
/// for data-driven conformance cases.
/// </summary>
internal sealed class RequiresOpenAiSpecTheoryAttribute : TheoryAttribute
{
    public RequiresOpenAiSpecTheoryAttribute()
    {
        if (!SpecCatalog.HasOpenAiSpec)
        {
            Skip = "The vendored OpenAI specification (API Specs/openai-openapi) is not present; "
                + "it is gitignored reference content, so this conformance test is skipped.";
        }
    }
}