using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Briefs.Tests;

public class ReviewBriefBuilderTests
{
    private static Ticket MinimalTicket() => new Ticket(
        Id: "TLB-1",
        Uuid: "test-uuid-1",
        Title: "Test ticket",
        Type: "feature",
        State: TicketState.Ready,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "<h2>Plan</h2><p>Implement the feature.</p>",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: null);

    private static GitDiff MinimalDiff() => new GitDiff(
        FromRef: "origin/main",
        ToRef: "feature/test",
        Entries: Array.Empty<DiffEntry>());

    private static WorkerResult MinimalImplementerResult() => new WorkerResult(
        Status: Status.Ok,
        Summary: "Implementation completed successfully.",
        FilesChanged: Array.Empty<string>(),
        FailureReason: null,
        Metadata: new Dictionary<string, object>());

    [Fact]
    public void Build_MinimalInputs_ReturnsReviewBrief()
    {
        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            MinimalDiff(),
            MinimalImplementerResult(),
            Array.Empty<CheckResult>());

        Assert.Equal(Phase.Review, brief.Phase);
        Assert.Equal("TLB-1", brief.TicketId);
        Assert.Empty(brief.AllowedWrites);
        Assert.Empty(brief.RelevantFiles);
    }

    [Fact]
    public void Build_Context_ContainsFeatureBranchBaseRefAndFilesChangedCount()
    {
        var diff = MinimalDiff() with
        {
            FromRef = "origin/main",
            ToRef = "ticket/tlb-1-test",
            Entries = new[]
            {
                new DiffEntry(
                    Path: "file1.cs",
                    Kind: DiffKind.Added,
                    OldPath: null,
                    LinesAdded: 10,
                    LinesRemoved: 0,
                    PatchContent: null)
            }
        };

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            diff,
            MinimalImplementerResult(),
            Array.Empty<CheckResult>());

        Assert.Equal("ticket/tlb-1-test", brief.Context["feature_branch"]);
        Assert.Equal("origin/main", brief.Context["base_ref"]);
        Assert.Equal("1", brief.Context["files_changed_count"]);
    }

    [Fact]
    public void Build_Instruction_ContainsWorkerResultEnvelopeWithVerdictRationaleChecksFailed()
    {
        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            MinimalDiff(),
            MinimalImplementerResult(),
            Array.Empty<CheckResult>());

        Assert.Contains("WORKER_RESULT", brief.Instruction);
        Assert.Contains("verdict", brief.Instruction);
        Assert.Contains("rationale", brief.Instruction);
        Assert.Contains("checks_failed", brief.Instruction);
        Assert.Contains("Pass", brief.Instruction);
        Assert.Contains("Rework", brief.Instruction);
        Assert.Contains("Fail", brief.Instruction);
    }

    [Fact]
    public void Build_Instruction_IncludesTicketDescriptionHtmlVerbatim()
    {
        var ticket = MinimalTicket() with
        {
            DescriptionHtml = "<h2>Goal</h2><p>Do the thing</p><ul><li>Step one</li></ul>"
        };

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            ticket,
            MinimalDiff(),
            MinimalImplementerResult(),
            Array.Empty<CheckResult>());

        Assert.Contains("<h2>Goal</h2>", brief.Instruction);
        Assert.Contains("<p>Do the thing</p>", brief.Instruction);
        Assert.Contains("<li>Step one</li>", brief.Instruction);
    }

    [Fact]
    public void Build_Instruction_IncludesImplementerSummary()
    {
        var implementerResult = MinimalImplementerResult() with
        {
            Summary = "This is the custom implementer summary."
        };

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            MinimalDiff(),
            implementerResult,
            Array.Empty<CheckResult>());

        Assert.Contains("This is the custom implementer summary.", brief.Instruction);
        Assert.Contains("## Implementer summary", brief.Instruction);
    }

    [Fact]
    public void Build_CleanChecks_RendersAllChecksAsPass()
    {
        var checks = new[]
        {
            new CheckResult(
                Name: "unit-tests",
                Passed: true,
                ExitCode: 0,
                StdoutTail: "All tests passed",
                StderrTail: "",
                Elapsed: TimeSpan.FromSeconds(5.5)),
            new CheckResult(
                Name: "build",
                Passed: true,
                ExitCode: 0,
                StdoutTail: "Build succeeded",
                StderrTail: "",
                Elapsed: TimeSpan.FromSeconds(3.2))
        };

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            MinimalDiff(),
            MinimalImplementerResult(),
            checks);

        Assert.Contains("unit-tests: PASS", brief.Instruction);
        Assert.Contains("build: PASS", brief.Instruction);
        Assert.Contains("exit 0", brief.Instruction);
    }

    [Fact]
    public void Build_FailingChecks_RendersStderrTail()
    {
        var errorMessage = "Error: something went wrong in the build";
        var checks = new[]
        {
            new CheckResult(
                Name: "build",
                Passed: false,
                ExitCode: 1,
                StdoutTail: "Build started",
                StderrTail: errorMessage,
                Elapsed: TimeSpan.FromSeconds(2.1))
        };

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            MinimalDiff(),
            MinimalImplementerResult(),
            checks);

        Assert.Contains("build: FAIL", brief.Instruction);
        Assert.Contains("exit 1", brief.Instruction);
        Assert.Contains(errorMessage, brief.Instruction);
    }

    [Fact]
    public void Build_LargeDiff_TruncatesToStayUnderBudget()
    {
        var largePatches = new List<DiffEntry>();
        for (int i = 0; i < 30; i++)
        {
            var patch = new string('x', 100 * 1024);
            largePatches.Add(new DiffEntry(
                Path: $"file{i}.cs",
                Kind: DiffKind.Modified,
                OldPath: null,
                LinesAdded: 1000,
                LinesRemoved: 500,
                PatchContent: patch));
        }

        var diff = MinimalDiff() with { Entries = largePatches };

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            diff,
            MinimalImplementerResult(),
            Array.Empty<CheckResult>());

        Assert.True(brief.Instruction.Length < 55 * 1024, "Instruction should stay under ~55 KB budget");
        Assert.Contains("[truncated:", brief.Instruction);
    }

    [Fact]
    public void Build_EmptyDiff_StillEmitsBriefAskingReviewerToConfirm()
    {
        var diff = MinimalDiff() with { Entries = Array.Empty<DiffEntry>() };

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            diff,
            MinimalImplementerResult(),
            Array.Empty<CheckResult>());

        Assert.Contains("(no files changed)", brief.Instruction);
        Assert.Contains("confirm there is nothing to review", brief.Instruction);
        Assert.Equal(Phase.Review, brief.Phase);
    }

    [Fact]
    public void Build_RenamedEntry_RendersWasOldPath()
    {
        var diff = MinimalDiff() with
        {
            Entries = new[]
            {
                new DiffEntry(
                    Path: "NewName.cs",
                    Kind: DiffKind.Renamed,
                    OldPath: "OldName.cs",
                    LinesAdded: 0,
                    LinesRemoved: 0,
                    PatchContent: null)
            }
        };

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            diff,
            MinimalImplementerResult(),
            Array.Empty<CheckResult>());

        Assert.Contains("Renamed NewName.cs", brief.Instruction);
        Assert.Contains("(was: OldName.cs)", brief.Instruction);
    }

    [Fact]
    public void Build_DiffEntryWithNullPatchContent_RendersFileListEntryWithoutPatch()
    {
        var diff = MinimalDiff() with
        {
            Entries = new[]
            {
                new DiffEntry(
                    Path: "file.cs",
                    Kind: DiffKind.Added,
                    OldPath: null,
                    LinesAdded: 42,
                    LinesRemoved: 0,
                    PatchContent: null)
            }
        };

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            diff,
            MinimalImplementerResult(),
            Array.Empty<CheckResult>());

        Assert.Contains("Added file.cs (+42/-0)", brief.Instruction);
    }

    [Fact]
    public void Build_AllowedWrites_IsAlwaysEmpty()
    {
        var diff = MinimalDiff() with
        {
            Entries = new[]
            {
                new DiffEntry(
                    Path: "file.cs",
                    Kind: DiffKind.Modified,
                    OldPath: null,
                    LinesAdded: 10,
                    LinesRemoved: 5,
                    PatchContent: "some patch content")
            }
        };

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            diff,
            MinimalImplementerResult(),
            Array.Empty<CheckResult>());

        Assert.Empty(brief.AllowedWrites);
    }

    [Fact]
    public void Build_MatchesSnapshot_Original()
    {
        var expected = SnapshotLoader.Load("review-original.txt");

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            SnapshotFixtures.Ticket(),
            SnapshotFixtures.Diff(),
            SnapshotFixtures.ImplementerResult(),
            SnapshotFixtures.Checks());

        Assert.Equal(expected, brief.Instruction);
    }

    [Fact]
    public void Build_TemplateLoadable_NameIsRegistered()
    {
        var ex = Record.Exception(() => TemplateLoader.Load("claude-code", "review.md"));

        Assert.Null(ex);
    }
}
