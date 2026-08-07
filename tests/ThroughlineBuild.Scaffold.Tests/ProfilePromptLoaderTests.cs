using ThroughlineBuild.Scaffold;
using Xunit;

namespace ThroughlineBuild.Scaffold.Tests;

/// <summary>
/// Guards the repository profile prompt contract consumed by an interactive agent.
/// </summary>
public class ProfilePromptLoaderTests
{
    [Fact]
    public void RepositoryPrompt_UsesTheSharedRulesButRequestsPlainJson()
    {
        var prompt = ProfilePromptLoader.LoadRepository();

        Assert.Contains("Interrogate the repository itself", prompt);
        Assert.Contains("setupFiles", prompt);
        Assert.Contains("contract_authority", prompt);
        Assert.Contains("Return ONLY the JSON object", prompt);
        Assert.DoesNotContain("{{op_doc_markdown}}", prompt);
    }
}
