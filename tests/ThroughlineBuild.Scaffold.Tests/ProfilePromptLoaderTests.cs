using ThroughlineBuild.Scaffold;
using Xunit;

namespace ThroughlineBuild.Scaffold.Tests;

/// <summary>
/// Guards the derive-profile prompt's CONTRACT, not the LLM's output. The deriver's actual file
/// selection is the worker's and is faked in <see cref="ScaffoldProfileDeriverTests"/>; what we can
/// pin deterministically is that the prompt still INSTRUCTS the worker to put the test harness/setup
/// file into convention_files, and does so stack-agnostically (by the runner's setup mechanism, not
/// by language). This is the regression the fix exists to prevent: experiment 3 derived a bundle that
/// omitted the auto-loaded setup file, so every implement brief re-read it.
/// </summary>
public class ProfilePromptLoaderTests
{
    [Fact]
    public void Load_ReturnsNonEmptyTemplate_WithOpDocPlaceholder()
    {
        var prompt = ProfilePromptLoader.Load();

        Assert.False(string.IsNullOrWhiteSpace(prompt));
        Assert.Contains("{{op_doc_markdown}}", prompt);
        Assert.Contains("convention_files", prompt);
    }

    [Fact]
    public void Prompt_MandatesTheHarnessSetupFileInConventionFiles()
    {
        var prompt = ProfilePromptLoader.Load();

        // The harness/setup file must be called for explicitly and made non-optional.
        Assert.Contains("harness/setup file", prompt);
        Assert.Contains("MANDATORY", prompt);
    }

    [Fact]
    public void Prompt_IdentifiesTheSetupFileGenerically_AcrossStacks()
    {
        var prompt = ProfilePromptLoader.Load();

        // Stack-agnostic identification: name the runner's own setup mechanism for JS, Python, and
        // .NET so the instruction is not a single-stack assumption. (See the experiment harness's #1
        // design constraint: stack specifics live in the derived data/prompt, not engine code.)
        Assert.Contains("setupFiles", prompt);   // vitest/jest
        Assert.Contains("conftest.py", prompt);  // pytest
        Assert.Contains("xUnit", prompt);        // .NET
    }

    [Fact]
    public void RepositoryPrompt_UsesTheSharedRulesButRequestsPlainJson()
    {
        var prompt = ProfilePromptLoader.LoadRepository();

        Assert.Contains("Interrogate the repository itself", prompt);
        Assert.Contains("setupFiles", prompt);
        Assert.Contains("Return ONLY the JSON object", prompt);
        Assert.DoesNotContain("{{op_doc_markdown}}", prompt);
    }
}
