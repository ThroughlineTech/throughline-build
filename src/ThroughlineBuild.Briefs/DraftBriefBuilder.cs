namespace ThroughlineBuild.Briefs;

/// <summary>
/// Builds the drafter brief by substituting operator text into the draft.md template.
/// </summary>
public static class DraftBriefBuilder
{
    /// <summary>
    /// Renders the draft.md template with the supplied operator text substituted verbatim.
    /// </summary>
    /// <param name="operatorText">Free-form operator input to embed in the brief.</param>
    /// <returns>The rendered brief string ready to pass to IWorkerAgent.</returns>
    public static string Build(string operatorText)
    {
        var vars = new Dictionary<string, string>
        {
            ["operator_text"] = operatorText
        };

        return TemplateLoader.Load("draft.md").Substitute(vars);
    }
}
