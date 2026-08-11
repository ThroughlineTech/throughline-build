using System.Text;
using System.Text.Json;
using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Commands;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

[Collection("Cli Tests Environment")]
public sealed class ConductorCommandTests
{
    private const string ValidInvariantToml = """
        [[conductor.review.invariants]]
        id = "contracts-io-free"
        statement = "The contracts project remains free of file and network I/O."
        paths = ["src/ThroughlineBuild.Contracts/**"]
        blocks_done = true

        [[conductor.review.invariants]]
        id = "aot-json"
        statement = "CLI JSON output uses source-generated serializer contexts."
        paths = ["src/ThroughlineBuild.Cli/**"]
        blocks_done = true
        """;

    [Fact]
    public void Prompt_IsCachedLfPinnedAndWorkerFree()
    {
        var first = ConductorPromptLoader.Load();
        var second = ConductorPromptLoader.Load();
        var output = new StringWriter();

        var exit = ConductorCommand.Execute(
            ["conductor", "prompt"],
            json: false,
            Directory.GetCurrentDirectory(),
            TextReader.Null,
            output,
            TextWriter.Null);

        Assert.Equal(0, exit);
        Assert.Same(first, second);
        Assert.Equal(first, output.ToString());
        Assert.DoesNotContain('\r', first);
        Assert.EndsWith("\n", first, StringComparison.Ordinal);
        Assert.Contains("Return ONLY TOML", first, StringComparison.Ordinal);
        Assert.Contains("Do not write or modify any files", first, StringComparison.Ordinal);
        Assert.Contains("Produce 2-5 invariants", first, StringComparison.Ordinal);
        Assert.Contains("Do not contradict", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Prompt_JsonUsesSourceGeneratedEnvelope()
    {
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
        var output = new StringWriter();

        var exit = ConductorCommand.Execute(
            ["conductor", "prompt"],
            json: true,
            Directory.GetCurrentDirectory(),
            TextReader.Null,
            output,
            TextWriter.Null);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(output.ToString());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(
            ConductorPromptLoader.Load(),
            doc.RootElement.GetProperty("data").GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task CliPrompt_RunsBeforeConfigWithoutConstructingWorker()
    {
        var repo = CreateRepoWithoutConductor();

        try
        {
            var result = await RunCliInDirectoryAsync(repo, ["conductor", "prompt", "--json"]);

            Assert.Equal(0, result.Exit);
            Assert.Equal(string.Empty, result.Stderr);
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Registry_ConductorIsDistinctAndRunsBeforeConfig()
    {
        var registry = CliVerbRegistryFactory.Build();

        Assert.True(registry.TryGet("conductor", out var verb));
        Assert.NotNull(verb);
        Assert.Equal(CliVerbKind.Conductor, verb.Kind);
        Assert.True(verb.RunsBeforeConfig);
    }

    [Fact]
    public void Apply_ReplacesOnlyInvariantRunAndPreservesCrLfBoundaryBytes()
    {
        var prefix = """
            [conductor]
            min_build_version = "0.1.0"
            branch_prefix = "ticket"
            ticket_prefix = "TLB"
            source_roots = ["src", "tests"]
            architecture_map = "docs/architecture.md"
            rework_cap = 3

            # invariant lead comment
            """.Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
        var oldRun = """
            [[conductor.review.invariants]]
            id = "old"
            statement = "Old invariant."
            blocks_done = true

            """.Replace("\n", "\r\n", StringComparison.Ordinal);
        var suffix = """
            [conductor.review.escalation]
            # preserve this comment exactly
            model_size = "large"
            paths = ["src/**"]

            [constellation]
            platform = "dotnet-cli"
            contract_authority = "src/ThroughlineBuild.Contracts"
            """.Replace("\n", "\r\n", StringComparison.Ordinal);
        var repo = CreateRepo(prefix + oldRun + suffix);
        var output = new StringWriter();

        try
        {
            var exit = ConductorCommand.Execute(
                ["conductor", "apply", "-"],
                json: true,
                repo,
                new StringReader(ValidInvariantToml),
                output,
                TextWriter.Null);

            Assert.Equal(0, exit);
            var actual = File.ReadAllBytes(PathFor(repo, ".build/conductor.toml"));
            var expected = Encoding.UTF8.GetBytes(
                prefix +
                ValidInvariantToml.Replace("\n", "\r\n", StringComparison.Ordinal) +
                "\r\n\r\n" +
                suffix);
            Assert.Equal(expected, actual);

            using var doc = JsonDocument.Parse(output.ToString());
            var data = doc.RootElement.GetProperty("data");
            Assert.Equal(2, data.GetProperty("invariantCount").GetInt32());
            Assert.True(data.GetProperty("changed").GetBoolean());
            Assert.EndsWith(".build\\conductor.toml", data.GetProperty("conductorPath").GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Apply_ReadsRelativeFileAndReportsUnchangedOnIdenticalSecondApply()
    {
        var repo = CreateRepo();
        File.WriteAllText(Path.Combine(repo, "invariants.toml"), ValidInvariantToml, new UTF8Encoding(false));

        try
        {
            var first = ExecuteApply(repo, "invariants.toml", TextReader.Null, json: false);
            var second = ExecuteApply(repo, "invariants.toml", TextReader.Null, json: true);

            Assert.Equal(0, first.Exit);
            Assert.Contains("applied 2 review invariants", first.Stdout, StringComparison.Ordinal);
            Assert.Equal(0, second.Exit);
            using var doc = JsonDocument.Parse(second.Stdout);
            Assert.False(doc.RootElement.GetProperty("data").GetProperty("changed").GetBoolean());
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    public static TheoryData<string, string> InvalidInputs => new()
    {
        {
            ValidInvariantToml.Replace(
                "The contracts project remains free of file and network I/O.",
                "Replace this sentence with a true review invariant for this repository.",
                StringComparison.Ordinal),
            "placeholder"
        },
        {
            ValidInvariantToml.Replace(
                "The contracts project remains free of file and network I/O.",
                "State a true, repository-derived invariant here.",
                StringComparison.Ordinal),
            "placeholder"
        },
        { "Explain these invariants in prose.", "must contain" },
        { $"```toml\n{ValidInvariantToml}\n```", "fences" },
        { "id = \"no-block\"\nstatement = \"No invariant header.\"", "must contain" },
        { "[[conductor.review.invariants]]\nid = \"broken", "between 2 and 5" },
        { ValidInvariantToml + "\n\n[constellation]\nplatform = \"ios\"", "extra TOML section" },
    };

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void Apply_InvalidInputFailsWithoutChangingConductor(string submitted, string expectedMessage)
    {
        var repo = CreateRepo();
        var conductorPath = PathFor(repo, ".build/conductor.toml");
        var before = File.ReadAllBytes(conductorPath);

        try
        {
            var result = ExecuteApply(repo, "-", new StringReader(submitted), json: true);

            Assert.Equal(1, result.Exit);
            Assert.Equal(before, File.ReadAllBytes(conductorPath));
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains(
                expectedMessage,
                doc.RootElement.GetProperty("error").GetProperty("message").GetString(),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Apply_MalformedTwoBlockTomlFailsWithoutWriting()
    {
        var repo = CreateRepo();
        var path = PathFor(repo, ".build/conductor.toml");
        var before = File.ReadAllBytes(path);
        var malformed = """
            [[conductor.review.invariants]]
            id = "one"
            statement = "Valid first."

            [[conductor.review.invariants]]
            id = "two"
            statement = "unterminated
            """;

        try
        {
            var result = ExecuteApply(repo, "-", new StringReader(malformed), json: false);

            Assert.Equal(1, result.Exit);
            Assert.Contains("malformed", result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Apply_MissingConductorIsConfigExitTwo()
    {
        var repo = CreateRepoWithoutConductor();

        try
        {
            var result = ExecuteApply(repo, "-", new StringReader(ValidInvariantToml), json: true);

            Assert.Equal(2, result.Exit);
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.Equal("config_error", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    public static TheoryData<string[]> InvalidArgumentSets => new()
    {
        new[] { "conductor" },
        new[] { "conductor", "missing" },
        new[] { "conductor", "apply" },
        new[] { "conductor", "prompt", "extra" },
    };

    [Theory]
    [MemberData(nameof(InvalidArgumentSets))]
    public void InvalidArgumentsExitTwo(string[] args)
    {
        var result = Execute(args, Directory.GetCurrentDirectory(), TextReader.Null, json: true);

        Assert.Equal(2, result.Exit);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal("usage", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static (int Exit, string Stdout, string Stderr) ExecuteApply(
        string repo,
        string path,
        TextReader input,
        bool json) =>
        Execute(["conductor", "apply", path], repo, input, json);

    private static (int Exit, string Stdout, string Stderr) Execute(
        IReadOnlyList<string> args,
        string startDirectory,
        TextReader input,
        bool json)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = ConductorCommand.Execute(args, json, startDirectory, input, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static string CreateRepo(string? conductor = null)
    {
        var repo = CreateRepoWithoutConductor();
        var build = Path.Combine(repo, ".build");
        Directory.CreateDirectory(build);
        File.WriteAllText(
            Path.Combine(build, "conductor.toml"),
            conductor ?? ValidConductorToml,
            new UTF8Encoding(false));
        return repo;
    }

    private static string CreateRepoWithoutConductor()
    {
        var repo = Path.Combine(Path.GetTempPath(), "conductor-command-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        return repo;
    }

    private static string PathFor(string repository, string relativePath) =>
        Path.Combine(repository, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static async Task<(int Exit, string Stdout, string Stderr)> RunCliInDirectoryAsync(
        string directory,
        string[] args)
    {
        var originalDirectory = Directory.GetCurrentDirectory();
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        try
        {
            Directory.SetCurrentDirectory(directory);
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = await CliApplication.RunAsync(
                args,
                (_, _) => throw new InvalidOperationException("worker must not be constructed"),
                new InProcessCliConsole(TextReader.Null, stdout, stderr));
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private const string ValidConductorToml = """
        [conductor]
        min_build_version = "0.1.0"
        branch_prefix = "ticket"
        ticket_prefix = "TLB"
        source_roots = ["src", "tests"]
        architecture_map = "docs/architecture.md"
        rework_cap = 3

        [[conductor.review.invariants]]
        id = "old"
        statement = "Old repository invariant."
        blocks_done = true

        [conductor.review.escalation]
        model_size = "large"
        paths = ["src/**"]

        [constellation]
        platform = "dotnet-cli"
        contract_authority = "src/ThroughlineBuild.Contracts"
        """;
}
