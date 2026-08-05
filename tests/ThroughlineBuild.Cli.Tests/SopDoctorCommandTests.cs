using System.Text.Json;
using ThroughlineBuild.Cli.Json;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Git;
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

    [Fact]
    public async Task CliSopList_ReportsEmbeddedSopsVersionsAndCatalogPaths()
    {
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
        var repo = CreateRepo();
        var (exit, stdout, stderr) = await RunCliInDirectoryAsync(repo, ["sop", "list", "--json"]);

        try
        {
            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stderr);
            using var doc = JsonDocument.Parse(stdout);
            var rows = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
            Assert.Contains(rows, row =>
                row.GetProperty("name").GetString() == "run-backlog" &&
                row.GetProperty("version").GetString() == BuildVersion.Current);
            var runBacklog = rows.Single(row => row.GetProperty("name").GetString() == "run-backlog");
            var ownedPaths = runBacklog.GetProperty("ownedPaths").EnumerateArray().ToList();
            Assert.Contains(ownedPaths, path =>
                path.GetProperty("path").GetString() == ".claude/commands/run-backlog.md" &&
                path.GetProperty("class").GetString() == "emitted" &&
                !string.IsNullOrWhiteSpace(path.GetProperty("expectedContentHash").GetString()));
            Assert.Contains(ownedPaths, path =>
                path.GetProperty("path").GetString() == ".build/conductor.toml" &&
                path.GetProperty("class").GetString() == "scaffolded" &&
                !path.TryGetProperty("expectedContentHash", out _));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task CliSopBrief_RunBacklog_EmitsProcedureConductorVersionsAndDoctor()
    {
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
        var repo = CreateRepo();
        var (exit, stdout, stderr) = await RunCliInDirectoryAsync(repo, ["sop", "brief", "run-backlog", "--json"]);

        try
        {
            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stderr);
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean());
            var data = root.GetProperty("data");
            Assert.True(data.GetProperty("ready").GetBoolean());
            Assert.Equal(SopBundleCatalog.SchemaVersion, data.GetProperty("sopSchemaVersion").GetInt32());
            Assert.Equal(BuildVersion.Current, data.GetProperty("sopVersion").GetString());
            Assert.Equal(BuildVersion.Current, data.GetProperty("binaryVersion").GetString());
            Assert.Equal("TLB", data.GetProperty("conductor").GetProperty("ticketPrefix").GetString());
            Assert.True(data.GetProperty("doctor").GetProperty("passed").GetBoolean());

            var text = data.GetProperty("sopText").GetString();
            Assert.Contains("# The ticket transaction (universal)", text);
            Assert.Contains("`build waves` accepts `verifiedExternalDeps` as an ASSERTION", text);
            Assert.Contains("Re-check this at the start of each wave", text);
            Assert.Contains("### 2.1 Semantic checkpoint classification", text);
            Assert.Contains("The execution contract records all of the following in the ticket body before code edits", text);
            Assert.Contains("A child inherits its parent's intent", text);
            Assert.Contains("A clear contract violation is an implementation defect", text);
            Assert.Contains("If one semantic miss repeats after rework", text);
            Assert.Contains("semantic contract missing / authority or parent-intent conflict", text);
            Assert.Contains("structured prose: doctor validates their shape only", text);
            Assert.DoesNotContain("](ticket-transaction.md)", text);
            Assert.DoesNotContain("](fan-out-scheduling.md)", text);
            Assert.Equal(2, CountOccurrences(text!, "](#sop-resource-ticket-transaction-md)"));
            Assert.Equal(2, CountOccurrences(text!, "](#sop-resource-fan-out-scheduling-md)"));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task CliSopBrief_CrossImpact_EmitsCrossImpactProcedure()
    {
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
        var repo = CreateRepo();
        var (exit, stdout, stderr) = await RunCliInDirectoryAsync(repo, ["sop", "brief", "cross-impact"]);

        try
        {
            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stderr);
            using var doc = JsonDocument.Parse(stdout);
            var text = doc.RootElement.GetProperty("data").GetProperty("sopText").GetString();
            Assert.Contains("# Cross-impact", text);
            Assert.Contains("DRAFT - not silently create", text);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task CliSopBrief_InvalidDoctor_FailsClosedWithoutSopText()
    {
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
        var conductor = ValidConductorToml.Replace(
            "min_build_version = \"0.1.0\"",
            "min_build_version = \"9.0.0\"");
        var repo = CreateRepo(conductor);
        var (exit, stdout, stderr) = await RunCliInDirectoryAsync(repo, ["sop", "brief", "run-backlog", "--json"]);

        try
        {
            Assert.Equal(1, exit);
            Assert.Equal(string.Empty, stderr);
            using var doc = JsonDocument.Parse(stdout);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            var data = doc.RootElement.GetProperty("data");
            Assert.False(data.GetProperty("ready").GetBoolean());
            Assert.False(data.TryGetProperty("sopText", out _));
            Assert.False(data.GetProperty("doctor").GetProperty("passed").GetBoolean());
            Assert.Contains(
                "conductor.min_build_version.newer_than_binary",
                FindingCodes(data.GetProperty("doctor")));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task CliSopBrief_UnknownSop_UsesDistinctExitCodeAndJsonError()
    {
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);
        var repo = CreateRepo();
        var (exit, stdout, stderr) = await RunCliInDirectoryAsync(repo, ["sop", "brief", "missing", "--json"]);

        try
        {
            Assert.Equal(9, exit);
            Assert.Equal(string.Empty, stderr);
            using var doc = JsonDocument.Parse(stdout);
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("unknown_sop", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Catalog_EmittedPathHashesMatchEmbeddedResources()
    {
        foreach (var entry in SopBundleCatalog.All)
        {
            foreach (var ownedPath in entry.OwnedPaths.Where(path => path.Class == SopBundleCatalog.EmittedPathClass))
            {
                Assert.False(string.IsNullOrWhiteSpace(ownedPath.ResourceName));
                Assert.False(string.IsNullOrWhiteSpace(ownedPath.ExpectedContentHash));
                var content = SopResourceLoader.LoadResource(ownedPath.ResourceName!);
                Assert.Equal(
                    ownedPath.ExpectedContentHash,
                    SopResourceLoader.ComputeSha256(content));
            }
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
    [InlineData(
        "ticket_prefix = \"TLB\"",
        "ticket_prefix = \"TICKET\"",
        "conductor.ticket_prefix.placeholder",
        "conductor.ticket_prefix",
        "replace it with the prefix used by this repository's ticket IDs")]
    [InlineData(
        "statement = \"CLI JSON output uses source-generated JsonSerializerContext.\"",
        "statement = \"Replace this sentence with a true review invariant for this repository.\"",
        "conductor.review.invariants.statement.placeholder",
        "conductor.review.invariants[1].statement",
        "replace it with a true review invariant for this repository")]
    [InlineData(
        "platform = \"dotnet-cli\"",
        "platform = \"unknown\"",
        "constellation.platform.placeholder",
        "constellation.platform",
        "replace it with the repository's actual platform identifier")]
    public void Doctor_FailsOnExactConductorScaffoldPlaceholders(
        string original,
        string placeholder,
        string expectedCode,
        string expectedPath,
        string expectedAction)
    {
        var repo = CreateRepo(ValidConductorToml.Replace(original, placeholder));
        var report = SopDoctorCommand.RunDoctor(repo, "0.1.0+test");

        try
        {
            Assert.False(report.Passed);
            var finding = Assert.Single(report.Findings, finding => finding.Code == expectedCode);
            Assert.Equal(expectedPath, finding.Path);
            Assert.Contains(".build/conductor.toml", finding.Message, StringComparison.Ordinal);
            Assert.Contains($"key '{expectedPath}'", finding.Message, StringComparison.Ordinal);
            Assert.Contains(expectedAction, finding.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Doctor_FailsOnUnknownConductorKeysIncludingMisspelledBlocksDone()
    {
        var conductor = ValidConductorToml
            .Replace("source_roots = [\"src\", \"tests\", \"docs\"]", "source_roots = [\"src\", \"tests\", \"docs\"]\nsource_rootz = [\"oops\"]")
            .Replace("blocks_done = true", "blocks-done = true");
        var repo = CreateRepo(conductor);
        var report = SopDoctorCommand.RunDoctor(repo, "0.1.0+test");

        try
        {
            Assert.False(report.Passed);
            Assert.Contains(report.Findings, finding =>
                finding.Code == "conductor.unknown_key" &&
                finding.Path == "conductor.source_rootz");
            Assert.Contains(report.Findings, finding =>
                finding.Code == "conductor.unknown_key" &&
                finding.Path == "conductor.review.invariants[0].blocks-done");
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Theory]
    [InlineData("# no review checks", "review.checks.empty")]
    [InlineData("[review]\nchecks = []", "review.checks.empty")]
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
    public void Doctor_FailsWhenReviewChecksHaveNoSetupOrGatingRole()
    {
        var repo = CreateRepo(configToml:
            """
            [review]

            [[review.checks]]
            name = "lint"
            executable = "dotnet"
            arguments = ["--info"]
            role = "advisory"
            """);
        var report = SopDoctorCommand.RunDoctor(repo, "0.1.0+test");

        try
        {
            Assert.False(report.Passed);
            Assert.Contains(report.Findings, finding => finding.Code == "review.checks.no_gating");
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Doctor_FailsWhenReviewCheckHasNoName()
    {
        var repo = CreateRepo(configToml:
            """
            [review]

            [[review.checks]]
            executable = "dotnet"
            role = "gating"
            """);
        var report = SopDoctorCommand.RunDoctor(repo, "0.1.0+test");

        try
        {
            Assert.False(report.Passed);
            Assert.Contains(report.Findings, finding => finding.Code == "review.checks.name.invalid");
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Doctor_FailsWhenReviewCheckRoleHasWhitespace()
    {
        var repo = CreateRepo(configToml:
            """
            [review]

            [[review.checks]]
            name = "unit"
            executable = "dotnet"
            role = " gating "
            """);
        var report = SopDoctorCommand.RunDoctor(repo, "0.1.0+test");

        try
        {
            Assert.False(report.Passed);
            Assert.Contains(report.Findings, finding => finding.Code == "review.checks.role.invalid");
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
    public void Doctor_UsesDistinctCodesForMissingConductorFileAndMissingConductorTable()
    {
        var withoutFile = CreateRepoWithoutConductor();
        var missingFile = SopDoctorCommand.RunDoctor(withoutFile, "0.1.0+test");
        var emptyFile = CreateRepo(conductorToml: "");
        var missingTable = SopDoctorCommand.RunDoctor(emptyFile, "0.1.0+test");

        try
        {
            Assert.Contains(missingFile.Findings, finding => finding.Code == "conductor.file.missing");
            Assert.DoesNotContain(missingFile.Findings, finding => finding.Code == "conductor.missing");
            Assert.Contains(missingTable.Findings, finding => finding.Code == "conductor.missing");
            Assert.DoesNotContain(missingTable.Findings, finding => finding.Code == "conductor.file.missing");
        }
        finally
        {
            TryDeleteDirectory(withoutFile);
            TryDeleteDirectory(emptyFile);
        }
    }

    [Fact]
    public async Task CliSopBrief_AfterClaudeOnlyInstall_DoesNotRequireUninstalledHostOrSopStubs()
    {
        var repo = CreateRepo();

        try
        {
            var install = await RunCliInDirectoryAsync(
                repo,
                ["sop", "install", "--sop", "run-backlog", "--host", "claude", "--json"]);
            Assert.Equal(0, install.Exit);
            Assert.True(File.Exists(PathFor(repo, ".claude/commands/run-backlog.md")));
            Assert.False(File.Exists(PathFor(repo, ".agents/skills/run-backlog/SKILL.md")));
            Assert.False(File.Exists(PathFor(repo, ".claude/commands/cross-impact.md")));

            var doctor = await RunCliInDirectoryAsync(repo, ["sop", "doctor", "--json"]);
            var brief = await RunCliInDirectoryAsync(repo, ["sop", "brief", "run-backlog", "--json"]);

            Assert.Equal(0, doctor.Exit);
            Assert.Equal(0, brief.Exit);
            using var doctorDoc = JsonDocument.Parse(doctor.Stdout);
            Assert.True(doctorDoc.RootElement.GetProperty("data").GetProperty("passed").GetBoolean());
            using var briefDoc = JsonDocument.Parse(brief.Stdout);
            Assert.True(briefDoc.RootElement.GetProperty("data").GetProperty("ready").GetBoolean());
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task CliSopBrief_AfterRunBacklogOnlyInstall_DoesNotRequireOtherSopStubs()
    {
        var repo = CreateRepo();

        try
        {
            var install = await RunCliInDirectoryAsync(repo, ["sop", "install", "--sop", "run-backlog", "--json"]);
            Assert.Equal(0, install.Exit);
            Assert.True(File.Exists(PathFor(repo, ".claude/commands/run-backlog.md")));
            Assert.True(File.Exists(PathFor(repo, ".agents/skills/run-backlog/SKILL.md")));
            Assert.False(File.Exists(PathFor(repo, ".claude/commands/cross-impact.md")));
            Assert.False(File.Exists(PathFor(repo, ".agents/skills/cross-impact/SKILL.md")));

            var doctor = await RunCliInDirectoryAsync(repo, ["sop", "doctor", "--json"]);
            var brief = await RunCliInDirectoryAsync(repo, ["sop", "brief", "run-backlog", "--json"]);

            Assert.Equal(0, doctor.Exit);
            Assert.Equal(0, brief.Exit);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task CliSopBrief_AfterHostUninstall_DoesNotRequireRemovedHostStub()
    {
        var repo = CreateRepo();

        try
        {
            Assert.Equal(0, (await RunCliInDirectoryAsync(repo, ["sop", "install", "--sop", "run-backlog", "--json"])).Exit);
            var uninstall = await RunCliInDirectoryAsync(
                repo,
                ["sop", "uninstall", "--sop", "run-backlog", "--host", "codex", "--json"]);
            Assert.Equal(0, uninstall.Exit);
            Assert.True(File.Exists(PathFor(repo, ".claude/commands/run-backlog.md")));
            Assert.False(File.Exists(PathFor(repo, ".agents/skills/run-backlog/SKILL.md")));

            var doctor = await RunCliInDirectoryAsync(repo, ["sop", "doctor", "--json"]);
            var brief = await RunCliInDirectoryAsync(repo, ["sop", "brief", "run-backlog", "--json"]);

            Assert.Equal(0, doctor.Exit);
            Assert.Equal(0, brief.Exit);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Doctor_FailsWhenManifestRecordedCatalogStubIsMissing()
    {
        var repo = CreateRepo();

        try
        {
            var install = SopInstaller.Run(
                "install",
                repo,
                [SopBundleCatalog.RunBacklog],
                "0.1.0+test",
                DateTimeOffset.UtcNow,
                host: "claude");
            Assert.True(install.Passed);
            File.Delete(PathFor(repo, ".claude/commands/run-backlog.md"));

            var report = SopDoctorCommand.RunDoctor(repo, "0.1.0+test");

            Assert.False(report.Passed);
            Assert.Contains(report.Findings, finding =>
                finding.Code == "sop.stub.missing" &&
                finding.Path == ".claude/commands/run-backlog.md");
            Assert.DoesNotContain(report.Findings, finding =>
                finding.Path == ".agents/skills/run-backlog/SKILL.md");
            Assert.DoesNotContain(report.Findings, finding =>
                finding.Path.Contains("cross-impact", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task CliSopDoctorAndBrief_FailForDeletedStubWithoutManifest()
    {
        var repo = CreateRepo();

        try
        {
            var install = await RunCliInDirectoryAsync(repo, ["sop", "install", "--json"]);
            Assert.Equal(0, install.Exit);
            await TrackEmittedStubsAsync(repo);
            File.Delete(PathFor(repo, ".build/sop-manifest.json"));
            File.Delete(PathFor(repo, ".claude/commands/run-backlog.md"));

            var doctor = await RunCliInDirectoryAsync(repo, ["sop", "doctor", "--json"]);
            var status = await RunCliInDirectoryAsync(repo, ["sop", "status", "--json"]);
            var brief = await RunCliInDirectoryAsync(repo, ["sop", "brief", "run-backlog", "--json"]);

            Assert.Equal(1, doctor.Exit);
            Assert.Equal(1, status.Exit);
            Assert.Equal(1, brief.Exit);
            using var doctorDoc = JsonDocument.Parse(doctor.Stdout);
            var doctorData = doctorDoc.RootElement.GetProperty("data");
            Assert.False(doctorData.GetProperty("passed").GetBoolean());
            Assert.Contains(doctorData.GetProperty("findings").EnumerateArray(), finding =>
                finding.GetProperty("code").GetString() == "sop.stub.missing" &&
                finding.GetProperty("path").GetString() == ".claude/commands/run-backlog.md");
            Assert.DoesNotContain(doctorData.GetProperty("findings").EnumerateArray(), finding =>
                finding.GetProperty("path").GetString() == ".agents/skills/run-backlog/SKILL.md");
            using var statusDoc = JsonDocument.Parse(status.Stdout);
            Assert.Contains(statusDoc.RootElement.GetProperty("data").GetProperty("results").EnumerateArray(), result =>
                result.GetProperty("status").GetString() == "missing" &&
                result.GetProperty("path").GetString() == ".claude/commands/run-backlog.md");
            using var briefDoc = JsonDocument.Parse(brief.Stdout);
            var briefData = briefDoc.RootElement.GetProperty("data");
            Assert.False(briefData.GetProperty("ready").GetBoolean());
            Assert.False(briefData.TryGetProperty("sopText", out _));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task CliSopDoctor_WithoutManifestDoesNotReportUninstalledCodexHost()
    {
        var repo = CreateRepo();

        try
        {
            var install = await RunCliInDirectoryAsync(
                repo,
                ["sop", "install", "--sop", "run-backlog", "--host", "claude", "--json"]);
            Assert.Equal(0, install.Exit);
            await TrackEmittedStubsAsync(repo, ".claude/commands/run-backlog.md");
            File.Delete(PathFor(repo, ".build/sop-manifest.json"));

            var doctor = await RunCliInDirectoryAsync(repo, ["sop", "doctor", "--json"]);

            Assert.Equal(0, doctor.Exit);
            using var doctorDoc = JsonDocument.Parse(doctor.Stdout);
            var findings = doctorDoc.RootElement.GetProperty("data").GetProperty("findings").EnumerateArray();
            Assert.DoesNotContain(findings, finding =>
                finding.GetProperty("path").GetString()?.StartsWith(".agents/skills/", StringComparison.Ordinal) == true);
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public async Task CliSopDoctorAndBrief_FailForTrackedWholeHostDeletionWithoutManifest()
    {
        var repo = CreateRepo();

        try
        {
            Assert.Equal(0, (await RunCliInDirectoryAsync(repo, ["sop", "install", "--json"])).Exit);
            await TrackEmittedStubsAsync(repo);
            File.Delete(PathFor(repo, ".build/sop-manifest.json"));
            Directory.Delete(PathFor(repo, ".claude"), recursive: true);

            var doctor = await RunCliInDirectoryAsync(repo, ["sop", "doctor", "--json"]);
            var status = await RunCliInDirectoryAsync(repo, ["sop", "status", "--json"]);
            var brief = await RunCliInDirectoryAsync(repo, ["sop", "brief", "run-backlog", "--json"]);

            Assert.Equal(1, doctor.Exit);
            Assert.Equal(1, status.Exit);
            Assert.Equal(1, brief.Exit);
            using var doctorDoc = JsonDocument.Parse(doctor.Stdout);
            var findings = doctorDoc.RootElement.GetProperty("data").GetProperty("findings").EnumerateArray().ToList();
            Assert.Contains(findings, finding =>
                finding.GetProperty("code").GetString() == "sop.stub.missing" &&
                finding.GetProperty("path").GetString() == ".claude/commands/run-backlog.md");
            Assert.Contains(findings, finding =>
                finding.GetProperty("code").GetString() == "sop.stub.missing" &&
                finding.GetProperty("path").GetString() == ".claude/commands/cross-impact.md");
            using var briefDoc = JsonDocument.Parse(brief.Stdout);
            var briefData = briefDoc.RootElement.GetProperty("data");
            Assert.False(briefData.GetProperty("ready").GetBoolean());
            Assert.False(briefData.TryGetProperty("sopText", out _));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Inspect_ReportsScopeUnavailableWhenIndexCannotBeConsulted()
    {
        var repo = CreateRepo();

        try
        {
            var install = SopInstaller.Run(
                "install",
                repo,
                SopBundleCatalog.All,
                "0.1.0+test",
                DateTimeOffset.UtcNow);
            Assert.True(install.Passed);
            File.Delete(PathFor(repo, ".build/sop-manifest.json"));
            Directory.Delete(PathFor(repo, ".claude"), recursive: true);

            var results = SopInstaller.InspectInstalledOrPresentEmittedStubs(
                repo,
                SopBundleCatalog.All,
                host: null,
                trackedPathProbe: (_, _) => GitTrackedPathProbe.Unavailable("git could not be started: not found"));

            Assert.Contains(results, result =>
                result.Status == "scope_unavailable" &&
                result.Path == ".claude/commands/run-backlog.md" &&
                result.Message.Contains("not found", StringComparison.Ordinal));
            Assert.Contains(results, result =>
                result.Status == "scope_unavailable" &&
                result.Path == ".claude/commands/cross-impact.md");

            // Stubs still on disk are answerable without the index, so they are graded normally.
            Assert.Contains(results, result =>
                result.Status == "clean" &&
                result.Path == ".agents/skills/run-backlog/SKILL.md");
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Doctor_FailsWhenIndexCannotBeConsultedForAbsentStub()
    {
        var repo = CreateRepo();

        try
        {
            var install = SopInstaller.Run(
                "install",
                repo,
                SopBundleCatalog.All,
                "0.1.0+test",
                DateTimeOffset.UtcNow);
            Assert.True(install.Passed);
            File.Delete(PathFor(repo, ".build/sop-manifest.json"));
            Directory.Delete(PathFor(repo, ".claude"), recursive: true);

            var unavailable = SopInstaller.InspectInstalledOrPresentEmittedStubs(
                repo,
                SopBundleCatalog.All,
                host: null,
                trackedPathProbe: (_, _) => GitTrackedPathProbe.Unavailable("git could not be started"));
            var notARepository = SopInstaller.InspectInstalledOrPresentEmittedStubs(
                repo,
                SopBundleCatalog.All,
                host: null,
                trackedPathProbe: (_, _) => GitTrackedPathProbe.NotARepository());

            // An unanswerable question blocks; a definite "there is no index here" does not,
            // because a tree without one cannot record install intent in the first place.
            Assert.Contains(unavailable, result => result.Status == "scope_unavailable");
            Assert.DoesNotContain(notARepository, result => result.Status == "scope_unavailable");
            Assert.DoesNotContain(notARepository, result =>
                result.Path.StartsWith(".claude/", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Doctor_FailsWhenCatalogStubIsModified()
    {
        var repo = CreateRepo();
        var stubPath = PathFor(repo, ".agents/skills/run-backlog/SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(stubPath)!);
        File.WriteAllText(stubPath, "local edit\n");

        try
        {
            var report = SopDoctorCommand.RunDoctor(repo, "0.1.0+test");

            Assert.False(report.Passed);
            Assert.Contains(report.Findings, finding =>
                finding.Code == "sop.stub.modified" &&
                finding.Path == ".agents/skills/run-backlog/SKILL.md");
        }
        finally
        {
            TryDeleteDirectory(repo);
        }
    }

    [Fact]
    public void Doctor_RefusesSymlinkedCatalogStub()
    {
        var repo = CreateRepo();
        var linkPath = PathFor(repo, ".claude/commands/run-backlog.md");
        var targetPath = Path.Combine(repo, "outside-target.md");
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        File.WriteAllText(targetPath, "target\n");

        try
        {
            try
            {
                File.CreateSymbolicLink(linkPath, targetPath);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            var report = SopDoctorCommand.RunDoctor(repo, "0.1.0+test");

            Assert.False(report.Passed);
            Assert.Contains(report.Findings, finding =>
                finding.Code == "sop.stub.unsafe_path" &&
                finding.Path == ".claude/commands/run-backlog.md");
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

    private static string CreateRepoWithoutConductor(string? configToml = ValidReviewChecksToml)
    {
        var repository = Path.Combine(
            Path.GetTempPath(),
            "sop-doctor-tests",
            Guid.NewGuid().ToString("N"));
        var buildDir = Path.Combine(repository, ".build");
        Directory.CreateDirectory(buildDir);
        if (configToml is not null)
            File.WriteAllText(Path.Combine(buildDir, "config.toml"), configToml);
        return repository;
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

    private static async Task TrackEmittedStubsAsync(string repository, params string[] paths)
    {
        await RunGitAsync(repository, "init");
        await RunGitAsync(repository, "config", "user.email", "sop-doctor-tests@example.test");
        await RunGitAsync(repository, "config", "user.name", "Sop Doctor Tests");
        var trackedPaths = paths.Length == 0
            ? SopBundleCatalog.All
                .SelectMany(entry => entry.OwnedPaths)
                .Where(path => path.Class == SopBundleCatalog.EmittedPathClass)
                .Select(path => path.Path)
                .ToArray()
            : paths;
        await RunGitAsync(repository, ["add", "--", .. trackedPaths]);
        await RunGitAsync(repository, "commit", "-m", "track emitted stubs");
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(
            process.ExitCode == 0,
            $"git {string.Join(" ", args)} failed: {await stderr}; stdout: {await stdout}");
    }

    private static IReadOnlyList<string> FindingCodes(JsonElement data) =>
        data.GetProperty("findings")
            .EnumerateArray()
            .Select(item => item.GetProperty("code").GetString() ?? string.Empty)
            .ToList();

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var start = 0;
        while (true)
        {
            var index = value.IndexOf(needle, start, StringComparison.Ordinal);
            if (index < 0)
                return count;
            count++;
            start = index + needle.Length;
        }
    }

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
