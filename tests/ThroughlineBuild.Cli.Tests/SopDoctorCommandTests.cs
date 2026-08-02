using System.Text.Json;
using ThroughlineBuild.Cli.Json;
using Xunit;

namespace ThroughlineBuild.Cli.Tests;

[Collection("Cli Tests Environment")]
public sealed class SopDoctorCommandTests
{
    private const string ValidConductorToml = """
        [conductor]
        min_build_version = "0.1.0"
        branch_prefix = "ticket"
        ticket_prefix = "TLB"
        source_roots = ["src", "tests", "docs"]
        architecture_map = "docs/throughline-build-architecture.md"
        rework_cap = 3

        [[conductor.review.invariants]]
        id = "contracts-io-free"
        statement = "ThroughlineBuild.Contracts stays free of file and network I/O."
        paths = ["src/ThroughlineBuild.Contracts/**"]
        blocks_done = true

        [[conductor.review.invariants]]
        id = "aot-json"
        statement = "CLI JSON output uses source-generated JsonSerializerContext."

        [conductor.review.escalation]
        model_size = "large"
        paths = ["src/ThroughlineBuild.Cli/**", "src/ThroughlineBuild.Contracts/**"]

        [constellation]
        platform = "dotnet-cli"
        contract_authority = "src/ThroughlineBuild.Contracts"
        """;

    private const string ValidReviewChecksToml = """
        [review]

        [[review.checks]]
        name = "unit"
        executable = "dotnet"
        arguments = ["test", "--no-restore"]
        role = "gating"
        """;

    [Fact]
    public void Doctor_ValidConfig_EmitsVersionedJsonEnvelopeAndConductorData()
    {
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
        var repo = CreateRepo();
        var output = new StringWriter();

        try
        {
            var exit = SopDoctorCommand.Execute(
                ["sop", "doctor"],
                json: true,
                repo,
                output,
                TextWriter.Null);

            Assert.Equal(0, exit);
            using var doc = JsonDocument.Parse(output.ToString());
            var root = doc.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.True(root.GetProperty("ok").GetBoolean());
            var data = root.GetProperty("data");
            Assert.True(data.GetProperty("passed").GetBoolean());
            Assert.Equal("shape_only", data.GetProperty("reviewInvariantMode").GetString());
            Assert.Empty(data.GetProperty("findings").EnumerateArray());

            var conductor = data.GetProperty("conductor");
            Assert.Equal("0.1.0", conductor.GetProperty("minBuildVersion").GetString());
            Assert.Equal("ticket", conductor.GetProperty("branchPrefix").GetString());
            Assert.Equal("TLB", conductor.GetProperty("ticketPrefix").GetString());
            Assert.Equal(3, conductor.GetProperty("reworkCap").GetInt32());
            Assert.Equal("docs/throughline-build-architecture.md", conductor.GetProperty("architectureMap").GetString());
            Assert.Equal("src", conductor.GetProperty("sourceRoots").EnumerateArray().First().GetString());
            Assert.Equal("large", conductor.GetProperty("review").GetProperty("escalation").GetProperty("modelSize").GetString());
            Assert.Equal("dotnet-cli", conductor.GetProperty("constellation").GetProperty("platform").GetString());
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task CliDoctor_LoadsConductorWithoutConfigToml()
    {
        var repo = CreateRepo(configToml: null);
        var (exit, stdout, stderr) = await RunCliInDirectoryAsync(repo, ["sop", "doctor", "--json"]);

        try
        {
            Assert.Equal(1, exit);
            Assert.Equal(string.Empty, stderr);
            using var doc = JsonDocument.Parse(stdout);
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            var data = doc.RootElement.GetProperty("data");
            Assert.False(data.GetProperty("passed").GetBoolean());
            Assert.Contains("review.checks.empty", FindingCodes(data));
            Assert.Equal("0.1.0", data.GetProperty("conductor").GetProperty("minBuildVersion").GetString());
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task CliDoctor_ConfigWithOnlyReviewChecksSkipsTicketingWorkersAndEvents()
    {
        var repo = CreateRepo();
        var (exit, stdout, stderr) = await RunCliInDirectoryAsync(repo, ["sop", "doctor", "--json"]);

        try
        {
            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stderr);
            using var doc = JsonDocument.Parse(stdout);
            Assert.True(doc.RootElement.GetProperty("data").GetProperty("passed").GetBoolean());
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Theory]
    [InlineData("", "conductor.min_build_version.missing")]
    [InlineData("min_build_version = \"soon\"", "conductor.min_build_version.malformed")]
    [InlineData("min_build_version = \"9.0.0\"", "conductor.min_build_version.newer_than_binary")]
    public void Doctor_FailsWhenMinBuildVersionIsAbsentMalformedOrTooNew(
        string replacementLine,
        string expectedCode)
    {
        var conductor = ValidConductorToml.Replace(
            "min_build_version = \"0.1.0\"",
            replacementLine);
        var repo = CreateRepo(conductor);
        var report = SopDoctorCommand.RunDoctor(repo, "0.1.0+test");

        try
        {
            Assert.False(report.Passed);
            Assert.Contains(report.Findings, finding => finding.Code == expectedCode);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Doctor_FailsOnDuplicateInvariantIdsAndEmptyStatements()
    {
        var conductor = ValidConductorToml
            .Replace("id = \"aot-json\"", "id = \"contracts-io-free\"")
            .Replace("statement = \"CLI JSON output uses source-generated JsonSerializerContext.\"", "statement = \"   \"");
        var repo = CreateRepo(conductor);
        var report = SopDoctorCommand.RunDoctor(repo, "0.1.0+test");

        try
        {
            Assert.False(report.Passed);
            Assert.Contains(report.Findings, finding => finding.Code == "conductor.review.invariants.duplicate_id");
            Assert.Contains(report.Findings, finding => finding.Code == "conductor.review.invariants[1].statement.invalid");
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Theory]
    [InlineData("# no review checks", "review.checks.empty")]
    [InlineData("[review]\n\n[[review.checks]]\nname = \"unit\"\nexecutable = \"   \"", "review.checks.command.missing")]
    [InlineData("[review]\n\n[[review.checks]]\nname = \"unit\"", "review.checks.command.missing")]
    public void Doctor_FailsWhenReviewChecksAreEmptyOrHaveNoRunnableCommand(
        string configToml,
        string expectedCode)
    {
        var repo = CreateRepo(configToml: configToml);
        var report = SopDoctorCommand.RunDoctor(repo, "0.1.0+test");

        try
        {
            Assert.False(report.Passed);
            Assert.Contains(report.Findings, finding => finding.Code == expectedCode);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Doctor_FailsWhenReworkCapLoosensBinaryDefault()
    {
        var conductor = ValidConductorToml.Replace("rework_cap = 3", "rework_cap = 4");
        var repo = CreateRepo(conductor);
        var report = SopDoctorCommand.RunDoctor(repo, "0.1.0+test");

        try
        {
            Assert.False(report.Passed);
            Assert.Contains(report.Findings, finding => finding.Code == "conductor.rework_cap.loosened");
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void WriteSopDoctor_UsesSourceGeneratedEnvelope()
    {
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
        var output = new StringWriter();
        var conductor = new ConductorConfig(
            "0.1.0",
            "ticket",
            "TLB",
            ["src"],
            "docs/throughline-build-architecture.md",
            3,
            new ConductorReviewConfig(
                [new ConductorReviewInvariant("shape", "A structured prose invariant.")],
                new ConductorReviewEscalation("large", ["src/**"])),
            new ConstellationConfig("dotnet-cli", "src/ThroughlineBuild.Contracts", []));
        var view = new SopDoctorView(
            RepositoryRoot: "C:\\repo",
            ConductorPath: "C:\\repo\\.build\\conductor.toml",
            ConfigPath: null,
            BuildVersion: "0.1.0+test",
            Passed: false,
            ReviewInvariantMode: "shape_only",
            Conductor: conductor,
            Findings: [new SopDoctorFinding("review.checks.empty", "review.checks", "missing")]);

        CliEnvelopeWriter.WriteSopDoctor(output, view);

        using var doc = JsonDocument.Parse(output.ToString());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("data").GetProperty("passed").GetBoolean());
        Assert.Equal(
            "shape",
            doc.RootElement
                .GetProperty("data")
                .GetProperty("conductor")
                .GetProperty("review")
                .GetProperty("invariants")
                .EnumerateArray()
                .Single()
                .GetProperty("id")
                .GetString());
    }

    private static string CreateRepo(
        string conductorToml = ValidConductorToml,
        string? configToml = ValidReviewChecksToml)
    {
        var repository = Path.Combine(
            Path.GetTempPath(),
            "sop-doctor-tests",
            Guid.NewGuid().ToString("N"));
        var buildDir = Path.Combine(repository, ".build");
        Directory.CreateDirectory(buildDir);
        File.WriteAllText(Path.Combine(buildDir, "conductor.toml"), conductorToml);
        if (configToml is not null)
            File.WriteAllText(Path.Combine(buildDir, "config.toml"), configToml);
        return repository;
    }

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
                (_, _) => throw new InvalidOperationException("worker must not be constructed"));
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    private static IReadOnlyList<string> FindingCodes(JsonElement data) =>
        data.GetProperty("findings")
            .EnumerateArray()
            .Select(item => item.GetProperty("code").GetString() ?? string.Empty)
            .ToList();

    private static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch when (attempt < 5)
            {
                Thread.Sleep(50);
            }
            catch
            {
                return;
            }
        }
    }
}
