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
        Assert.Contains("lowercase kebab-case", prompt);
        Assert.Contains("toolchain actually compiles or collects", prompt);
        Assert.Contains("Dependencies in the primary tree are human-managed", prompt);
        Assert.Contains("runs it exactly once after creating each lease", prompt);
        Assert.Contains("on EVERY gate invocation", prompt);
        Assert.Contains("NEVER emit `install_command`", prompt);
        Assert.Contains("Setup must not write tracked files", prompt);
        Assert.DoesNotContain("__tlb_probe", prompt);
        Assert.Contains("Return ONLY the JSON object", prompt);
        Assert.Contains("build install --profile .build/profile.json", prompt);
        Assert.DoesNotContain("build profile apply", prompt);
        Assert.DoesNotContain("{{op_doc_markdown}}", prompt);
    }
}
