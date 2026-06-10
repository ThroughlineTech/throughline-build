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
    public void Build_FailingChecks_RendersStdoutTail()
    {
        // Many toolchains (dotnet MSB1003, tsc, vite) write the fatal error to stdout, not stderr.
        // The review brief must surface stdout so the verifier can see the real failure.
        const string stdoutError = "MSBUILD : error MSB1003: Specify a project or solution file.";
        var checks = new[]
        {
            new CheckResult(
                Name: "build",
                Passed: false,
                ExitCode: 1,
                StdoutTail: stdoutError,
                StderrTail: "",
                Elapsed: TimeSpan.FromSeconds(1.2))
        };

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            MinimalDiff(),
            MinimalImplementerResult(),
            checks);

        Assert.Contains("build: FAIL", brief.Instruction);
        Assert.Contains(stdoutError, brief.Instruction);
    }

    [Fact]
    public void Build_LargeDiff_EmitsFetchDirectiveInsteadOfTruncating()
    {
        // A diff that exceeds the inline budget must NOT be truncated - a truncated patch
        // reads as "missing code" to the verifier and drives false-negative rework (TLB-477).
        // Instead the reviewer is told to pull the diff itself from the worktree.
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

        // No inlined patch body, no truncation marker.
        Assert.DoesNotContain("[truncated:", brief.Instruction);
        Assert.DoesNotContain(new string('x', 4096), brief.Instruction);
        // The brief stays small because the multi-MB patch is not inlined.
        Assert.True(brief.Instruction.Length < 16 * 1024, "Instruction should stay small when the diff is fetched on demand");
        // Reviewer is given the exact read-only command and the changed-file list.
        Assert.Contains("git diff origin/main...feature/test -- <path>", brief.Instruction);
        Assert.Contains("checked out in your working directory", brief.Instruction);
        Assert.Contains("file0.cs", brief.Instruction);
    }

    [Fact]
    public void Build_DiffWithinBudget_InlinesEveryPatchInFull()
    {
        // Under the inline budget, every file's patch is included verbatim with no truncation.
        var patchA = "@@ marker-alpha @@\n" + new string('a', 40 * 1024);
        var patchB = "@@ marker-beta @@\n" + new string('b', 40 * 1024);
        var diff = MinimalDiff() with
        {
            Entries = new[]
            {
                new DiffEntry("a.cs", DiffKind.Modified, null, 100, 50, patchA),
                new DiffEntry("b.cs", DiffKind.Modified, null, 100, 50, patchB)
            }
        };

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            diff,
            MinimalImplementerResult(),
            Array.Empty<CheckResult>());

        Assert.Contains("marker-alpha", brief.Instruction);
        Assert.Contains("marker-beta", brief.Instruction);
        Assert.Contains(patchA, brief.Instruction);
        Assert.Contains(patchB, brief.Instruction);
        Assert.DoesNotContain("[truncated:", brief.Instruction);
        // The fetch command is emitted only in fetch-on-demand mode, never when inlined.
        Assert.DoesNotContain("git diff origin/main...feature/test -- <path>", brief.Instruction);
    }

    [Fact]
    public void Build_EntriesWithoutPatchContent_EmitsFetchDirective()
    {
        // Changed files exist but no inline patch was captured: the reviewer must fetch it.
        var diff = MinimalDiff() with
        {
            Entries = new[]
            {
                new DiffEntry("a.cs", DiffKind.Modified, null, 10, 2, null),
                new DiffEntry("b.cs", DiffKind.Added, null, 30, 0, null)
            }
        };

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            diff,
            MinimalImplementerResult(),
            Array.Empty<CheckResult>());

        Assert.Contains("not captured", brief.Instruction);
        Assert.Contains("git diff origin/main...feature/test -- <path>", brief.Instruction);
        Assert.Contains("Modified a.cs", brief.Instruction);
        Assert.Contains("Added b.cs", brief.Instruction);
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

    // ---------------------------------------------------------------------------
    // Quiet-on-pass / loud-on-fail convention for automated checks.
    //
    // The brief becomes cached LLM context, so a passing check's stdout/stderr is
    // pure cache weight with zero actionable content. The convention: emit only a
    // one-line status for a passing check, and surface the captured tails ONLY when
    // a check fails. These tests pin that convention so a future change cannot start
    // dumping passing-check output into the brief.
    // ---------------------------------------------------------------------------

    [Fact]
    public void Build_PassingCheck_OmitsStdoutAndStderrTails()
    {
        var checks = new[]
        {
            new CheckResult("build", true, 0, "PASS_STDOUT_SENTINEL_XYZ", "PASS_STDERR_SENTINEL_XYZ", TimeSpan.FromSeconds(1))
        };

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            MinimalDiff(),
            MinimalImplementerResult(),
            checks);

        Assert.Contains("build: PASS", brief.Instruction);
        // Load-bearing: a passing check's captured output is never surfaced into the brief.
        Assert.DoesNotContain("PASS_STDOUT_SENTINEL_XYZ", brief.Instruction);
        Assert.DoesNotContain("PASS_STDERR_SENTINEL_XYZ", brief.Instruction);
    }

    [Fact]
    public void Build_FailingCheck_SurfacesStdoutAndStderrTails()
    {
        var checks = new[]
        {
            new CheckResult("build", false, 1, "FAIL_STDOUT_SENTINEL_XYZ", "FAIL_STDERR_SENTINEL_XYZ", TimeSpan.FromSeconds(1))
        };

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            MinimalDiff(),
            MinimalImplementerResult(),
            checks);

        Assert.Contains("build: FAIL", brief.Instruction);
        Assert.Contains("FAIL_STDOUT_SENTINEL_XYZ", brief.Instruction);
        Assert.Contains("FAIL_STDERR_SENTINEL_XYZ", brief.Instruction);
    }

    [Fact]
    public void Build_MixedChecks_OmitsPassingTailsButSurfacesFailingTails()
    {
        // Proves the omission is per-check, not all-or-nothing: a passing check next to a
        // failing check still has its tails dropped while the failing check's tails appear.
        var checks = new[]
        {
            new CheckResult("build", true, 0, "PASS_STDOUT_SENTINEL_XYZ", "PASS_STDERR_SENTINEL_XYZ", TimeSpan.FromSeconds(1)),
            new CheckResult("tests", false, 1, "FAIL_STDOUT_SENTINEL_XYZ", "FAIL_STDERR_SENTINEL_XYZ", TimeSpan.FromSeconds(1))
        };

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            MinimalDiff(),
            MinimalImplementerResult(),
            checks);

        Assert.Contains("build: PASS", brief.Instruction);
        Assert.Contains("tests: FAIL", brief.Instruction);
        Assert.DoesNotContain("PASS_STDOUT_SENTINEL_XYZ", brief.Instruction);
        Assert.DoesNotContain("PASS_STDERR_SENTINEL_XYZ", brief.Instruction);
        Assert.Contains("FAIL_STDOUT_SENTINEL_XYZ", brief.Instruction);
        Assert.Contains("FAIL_STDERR_SENTINEL_XYZ", brief.Instruction);
    }

    // ---------------------------------------------------------------------------
    // Advisory role split. Advisory failures are presented in their own explicitly-
    // framed informational section, never mixed in with gating results: presenting
    // a failing advisory check undifferentiated made the verifier Rework a chain to
    // death over a cosmetic lint finding (a downstream repository chain 25). These tests pin
    // the split and the framing.
    // ---------------------------------------------------------------------------

    [Fact]
    public void Build_FailingAdvisoryCheck_RendersInAdvisorySectionWithFraming()
    {
        var checks = new[]
        {
            new CheckResult("build", true, 0, "", "", TimeSpan.FromSeconds(1)),
            new CheckResult("lint", false, 1, "ADVISORY_STDOUT_SENTINEL", "", TimeSpan.FromSeconds(1), CheckRole.Advisory)
        };

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            MinimalDiff(),
            MinimalImplementerResult(),
            checks);

        Assert.Contains("### Advisory checks (informational)", brief.Instruction);
        Assert.Contains("do NOT list them in checks_failed", brief.Instruction);
        Assert.Contains("lint: FAIL", brief.Instruction);
        // The advisory failure's output is still shown (the verifier may note it).
        Assert.Contains("ADVISORY_STDOUT_SENTINEL", brief.Instruction);
        // The advisory check renders AFTER the advisory heading, not in the gating list.
        var headingIdx = brief.Instruction.IndexOf("### Advisory checks (informational)", StringComparison.Ordinal);
        var lintIdx = brief.Instruction.IndexOf("lint: FAIL", StringComparison.Ordinal);
        Assert.True(lintIdx > headingIdx);
    }

    [Fact]
    public void Build_NoAdvisoryChecks_NoAdvisorySection()
    {
        var checks = new[]
        {
            new CheckResult("build", false, 1, "broken", "", TimeSpan.FromSeconds(1))
        };

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            MinimalDiff(),
            MinimalImplementerResult(),
            checks);

        Assert.DoesNotContain("### Advisory checks (informational)", brief.Instruction);
    }

    [Fact]
    public void Build_AdvisoryOnlyChecks_MainSectionSaysNoGatingChecks()
    {
        var checks = new[]
        {
            new CheckResult("lint", false, 1, "warn", "", TimeSpan.FromSeconds(1), CheckRole.Advisory)
        };

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            MinimalDiff(),
            MinimalImplementerResult(),
            checks);

        Assert.Contains("(no gating checks configured)", brief.Instruction);
        Assert.Contains("### Advisory checks (informational)", brief.Instruction);
    }

    [Fact]
    public void Build_SetupCheck_StaysInMainSection()
    {
        // Setup failures hard-fail the gate, so they present alongside gating results.
        var checks = new[]
        {
            new CheckResult("codegen", false, 1, "setup broke", "", TimeSpan.FromSeconds(1), CheckRole.Setup)
        };

        var brief = ReviewBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            MinimalDiff(),
            MinimalImplementerResult(),
            checks);

        Assert.Contains("codegen: FAIL", brief.Instruction);
        Assert.DoesNotContain("### Advisory checks (informational)", brief.Instruction);
    }

    // BatchReviewBriefBuilder.BuildAutomatedChecksSection uses the identical per-check
    // pattern; pin the same convention there. Wiring the batch inputs is a single ticket
    // plus its BatchTicketResult and a base ref.
    private static BatchTicketResult MinimalBatchResult() => new BatchTicketResult(
        TicketId: "TLB-1",
        CommitSha: "abcdef1234567",
        StackPosition: 1,
        FilesChanged: Array.Empty<string>(),
        SummaryRef: "summary-ref");

    [Fact]
    public void BatchBuild_PassingCheck_OmitsStdoutAndStderrTails()
    {
        var checks = new[]
        {
            new CheckResult("build", true, 0, "PASS_STDOUT_SENTINEL_XYZ", "PASS_STDERR_SENTINEL_XYZ", TimeSpan.FromSeconds(1))
        };

        var brief = BatchReviewBriefBuilder.Build(
            "claude-code",
            new[] { MinimalTicket() },
            new[] { MinimalBatchResult() },
            "origin/main",
            MinimalDiff(),
            checks);

        Assert.Contains("build: PASS", brief.Instruction);
        Assert.DoesNotContain("PASS_STDOUT_SENTINEL_XYZ", brief.Instruction);
        Assert.DoesNotContain("PASS_STDERR_SENTINEL_XYZ", brief.Instruction);
    }

    [Fact]
    public void BatchBuild_FailingCheck_SurfacesStdoutAndStderrTails()
    {
        var checks = new[]
        {
            new CheckResult("build", false, 1, "FAIL_STDOUT_SENTINEL_XYZ", "FAIL_STDERR_SENTINEL_XYZ", TimeSpan.FromSeconds(1))
        };

        var brief = BatchReviewBriefBuilder.Build(
            "claude-code",
            new[] { MinimalTicket() },
            new[] { MinimalBatchResult() },
            "origin/main",
            MinimalDiff(),
            checks);

        Assert.Contains("build: FAIL", brief.Instruction);
        Assert.Contains("FAIL_STDOUT_SENTINEL_XYZ", brief.Instruction);
        Assert.Contains("FAIL_STDERR_SENTINEL_XYZ", brief.Instruction);
    }
}
