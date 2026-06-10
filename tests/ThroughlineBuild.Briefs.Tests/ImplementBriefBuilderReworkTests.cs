using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using Xunit;

namespace ThroughlineBuild.Briefs.Tests;

public class ImplementBriefBuilderReworkTests
{
    private static Ticket MinimalTicket() => new Ticket(
        Id: "TLB-1",
        Uuid: "test-uuid-1",
        Title: "Test ticket",
        Type: "feature",
        State: TicketState.Ready,
        Size: Size.S,
        Risk: Risk.Low,
        DescriptionHtml: "",
        Relations: Array.Empty<Relation>(),
        Labels: Array.Empty<string>(),
        ParentId: null);

    private static RepoState MinimalRepo() => new RepoState(
        MainSha: "abc1234",
        TopLevelEntries: Array.Empty<string>());

    private const string Branch = "ticket/tlb-1-test-ticket";
    private const string Worktree = "/repo/.worktrees/ticket-tlb-1-test-ticket";

    [Fact]
    public void Build_NullReviewFeedback_ReviewFeedbackSectionInContextIsEmptyString()
    {
        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: null);

        Assert.True(brief.Context.ContainsKey("review_feedback_section"));
        Assert.Equal("", brief.Context["review_feedback_section"]);
    }

    [Fact]
    public void Build_NullReviewFeedback_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: null));

        Assert.Null(ex);
    }

    [Fact]
    public void Build_WithReviewFeedback_ContextContainsRationale()
    {
        var feedback = new ReviewFeedback(
            Rationale: "The implementation missed edge cases.",
            ChecksFailed: new[] { "tests_pass" },
            ReworkRoundNumber: 1);

        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: feedback);

        Assert.Contains("The implementation missed edge cases.", brief.Context["review_feedback_section"]);
    }

    [Fact]
    public void Build_WithReviewFeedback_ContextContainsAllChecksFailed()
    {
        var feedback = new ReviewFeedback(
            Rationale: "Multiple checks failed.",
            ChecksFailed: new[] { "tests_pass", "coverage_ok", "no_warnings" },
            ReworkRoundNumber: 1);

        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: feedback);

        var section = brief.Context["review_feedback_section"];
        Assert.Contains("tests_pass", section);
        Assert.Contains("coverage_ok", section);
        Assert.Contains("no_warnings", section);
    }

    [Fact]
    public void Build_WithReviewFeedback_ContextContainsRoundNumber()
    {
        var feedback = new ReviewFeedback(
            Rationale: "Some feedback.",
            ChecksFailed: Array.Empty<string>(),
            ReworkRoundNumber: 2);

        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: feedback);

        Assert.Contains("2", brief.Context["review_feedback_section"]);
        Assert.Contains("Rework round 2", brief.Context["review_feedback_section"]);
    }

    [Fact]
    public void Build_WithReviewFeedback_SectionHeadingMatchesExpectedFormat()
    {
        var feedback = new ReviewFeedback(
            Rationale: "Rationale text.",
            ChecksFailed: new[] { "check_one" },
            ReworkRoundNumber: 1);

        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: feedback);

        Assert.Contains("## Rework round 1 - reviewer feedback", brief.Context["review_feedback_section"]);
    }

    [Fact]
    public void Build_WithReviewFeedback_EmptyChecksFailed_ShowsNone()
    {
        var feedback = new ReviewFeedback(
            Rationale: "General concerns only.",
            ChecksFailed: Array.Empty<string>(),
            ReworkRoundNumber: 1);

        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: feedback);

        Assert.Contains("(none)", brief.Context["review_feedback_section"]);
    }

    [Fact]
    public void Build_WithReworkContext_IncludesPriorSummaryAndTouchedFiles()
    {
        var feedback = new ReviewFeedback(
            Rationale: "Fix the missed tests.",
            ChecksFailed: new[] { "tests" },
            ReworkRoundNumber: 1);
        var reworkContext = new ReworkBriefContext(
            ImplementSummary: "Added the parser and CLI wiring.",
            TouchedFiles: new[] { "src/Parser.cs", "tests/ParserTests.cs" });

        var brief = ImplementBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            MinimalRepo(),
            Branch,
            Worktree,
            reviewFeedback: feedback,
            reworkContext: reworkContext);

        Assert.Contains("## Prior implement context", brief.Instruction);
        Assert.Contains("Added the parser and CLI wiring.", brief.Instruction);
        Assert.Contains("- src/Parser.cs", brief.Instruction);
        Assert.Contains("- tests/ParserTests.cs", brief.Instruction);
        Assert.Equal(new[] { "src/Parser.cs", "tests/ParserTests.cs" }, brief.RelevantFiles);
    }

    [Fact]
    public void Build_WithReworkContext_BoundsPriorSummary()
    {
        var feedback = new ReviewFeedback(
            Rationale: "Fix the missed tests.",
            ChecksFailed: new[] { "tests" },
            ReworkRoundNumber: 1);
        var longSummary = new string('x', 2100);
        var reworkContext = new ReworkBriefContext(
            ImplementSummary: longSummary,
            TouchedFiles: new[] { "src/Parser.cs" });

        var brief = ImplementBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            MinimalRepo(),
            Branch,
            Worktree,
            reviewFeedback: feedback,
            reworkContext: reworkContext);

        Assert.DoesNotContain(longSummary, brief.Instruction);
        Assert.Contains("[truncated: 100 more chars]", brief.Instruction);
    }

    [Fact]
    public void Build_InitialRound_IgnoresReworkContextWithoutFeedback()
    {
        var reworkContext = new ReworkBriefContext(
            ImplementSummary: "Should not appear.",
            TouchedFiles: new[] { "src/ShouldNotAppear.cs" });

        var brief = ImplementBriefBuilder.Build(
            "claude-code",
            MinimalTicket(),
            MinimalRepo(),
            Branch,
            Worktree,
            reviewFeedback: null,
            reworkContext: reworkContext);

        Assert.DoesNotContain("Prior implement context", brief.Instruction);
        Assert.DoesNotContain("Should not appear.", brief.Instruction);
        Assert.DoesNotContain("src/ShouldNotAppear.cs", brief.Instruction);
        Assert.Empty(brief.RelevantFiles);
    }

    [Fact]
    public void Build_SnapshotOriginal_StillPassesWithNullFeedback()
    {
        var expected = SnapshotLoader.Load("implement-original.txt");

        var brief = ImplementBriefBuilder.Build(
            "claude-code",
            SnapshotFixtures.Ticket(),
            SnapshotFixtures.Repo(),
            SnapshotFixtures.FixtureBranch,
            SnapshotFixtures.FixtureWorktree,
            reviewFeedback: null);

        Assert.Equal(expected, brief.Instruction);
    }

    // --- gate-originated rework ---

    [Fact]
    public void Build_GateFailedChecks_SectionHeadingIsGateFailure()
    {
        var feedback = new ReviewFeedback(
            Rationale: "gate: build failed",
            ChecksFailed: new[] { "build" },
            ReworkRoundNumber: 1,
            GateFailedChecks: new[]
            {
                new CheckResult("build", Passed: false, ExitCode: 1,
                    StdoutTail: "error CS0001: something", StderrTail: "",
                    Elapsed: TimeSpan.FromSeconds(2))
            });

        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: feedback);

        Assert.Contains("## Rework round 1 - gate failure", brief.Context["review_feedback_section"]);
        Assert.DoesNotContain("reviewer feedback", brief.Context["review_feedback_section"]);
    }

    [Fact]
    public void Build_GateFailedChecks_ContainsCheckNameAndExitCode()
    {
        var feedback = new ReviewFeedback(
            Rationale: "gate: tests failed",
            ChecksFailed: new[] { "tests" },
            ReworkRoundNumber: 1,
            GateFailedChecks: new[]
            {
                new CheckResult("tests", Passed: false, ExitCode: 2,
                    StdoutTail: "1 test failed", StderrTail: "",
                    Elapsed: TimeSpan.FromSeconds(5))
            });

        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: feedback);

        var section = brief.Context["review_feedback_section"];
        Assert.Contains("### Failed check: tests (exit 2)", section);
    }

    [Fact]
    public void Build_GateFailedChecks_ContainsStdoutAndStderrOutput()
    {
        var feedback = new ReviewFeedback(
            Rationale: "gate: tests failed",
            ChecksFailed: new[] { "tests" },
            ReworkRoundNumber: 1,
            GateFailedChecks: new[]
            {
                new CheckResult("tests", Passed: false, ExitCode: 1,
                    StdoutTail: "Test output line", StderrTail: "Stderr line",
                    Elapsed: TimeSpan.FromSeconds(3))
            });

        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: feedback);

        var section = brief.Context["review_feedback_section"];
        Assert.Contains("Test output line", section);
        Assert.Contains("Stderr line", section);
        Assert.Contains("stdout:", section);
        Assert.Contains("stderr:", section);
    }

    [Fact]
    public void Build_GateFailedChecks_EmptyStdoutTail_OmitsStdoutBlock()
    {
        var feedback = new ReviewFeedback(
            Rationale: "gate: build failed",
            ChecksFailed: new[] { "build" },
            ReworkRoundNumber: 1,
            GateFailedChecks: new[]
            {
                new CheckResult("build", Passed: false, ExitCode: 1,
                    StdoutTail: "", StderrTail: "error: symbol not found",
                    Elapsed: TimeSpan.FromSeconds(1))
            });

        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: feedback);

        var section = brief.Context["review_feedback_section"];
        Assert.DoesNotContain("stdout:", section);
        Assert.Contains("stderr:", section);
        Assert.Contains("error: symbol not found", section);
    }

    [Fact]
    public void Build_NullGateFailedChecks_UsesReviewerFeedbackPath()
    {
        var feedback = new ReviewFeedback(
            Rationale: "reviewer says fix it",
            ChecksFailed: new[] { "tests" },
            ReworkRoundNumber: 1,
            GateFailedChecks: null);

        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: feedback);

        Assert.Contains("## Rework round 1 - reviewer feedback", brief.Context["review_feedback_section"]);
    }

    [Fact]
    public void Build_GateFailedChecks_MultipleFailedChecks_AllRendered()
    {
        var feedback = new ReviewFeedback(
            Rationale: "gate: build, tests failed",
            ChecksFailed: new[] { "build", "tests" },
            ReworkRoundNumber: 2,
            GateFailedChecks: new[]
            {
                new CheckResult("build", Passed: false, ExitCode: 1,
                    StdoutTail: "build output", StderrTail: "",
                    Elapsed: TimeSpan.FromSeconds(1)),
                new CheckResult("tests", Passed: false, ExitCode: 1,
                    StdoutTail: "tests output", StderrTail: "",
                    Elapsed: TimeSpan.FromSeconds(2))
            });

        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: feedback);

        var section = brief.Context["review_feedback_section"];
        Assert.Contains("### Failed check: build (exit 1)", section);
        Assert.Contains("### Failed check: tests (exit 1)", section);
        Assert.Contains("build output", section);
        Assert.Contains("tests output", section);
        Assert.Contains("Rework round 2", section);
    }

    // --- check-failure briefs carry the oracle, not the theory ---
    // A reviewer's prose about WHY a check fails can confidently describe rules the tool
    // does not have (the a downstream repository chain-25 reviewer prescribed an import order that
    // ALSO failed the linter). The brief must carry the exact command, exit code, and
    // verbatim output, plus the instruction to re-run the command and trust its output.

    [Fact]
    public void Build_GateFailedChecks_RendersCommandLineAndRerunInstruction()
    {
        var feedback = new ReviewFeedback(
            Rationale: "gate: lint failed",
            ChecksFailed: new[] { "lint" },
            ReworkRoundNumber: 1,
            GateFailedChecks: new[]
            {
                new CheckResult("lint", Passed: false, ExitCode: 1,
                    StdoutTail: "file.swift:2:8 sorted_imports", StderrTail: "",
                    Elapsed: TimeSpan.FromSeconds(2),
                    CommandLine: "swiftlint --strict --no-cache")
            });

        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: feedback);

        var section = brief.Context["review_feedback_section"];
        Assert.Contains("Command: `swiftlint --strict --no-cache`", section);
        Assert.Contains("re-run each failed check's exact command", section);
        Assert.Contains("confirm it exits 0", section);
        Assert.Contains("trust the output", section);
    }

    [Fact]
    public void Build_GateFailedChecks_EmptyCommandLine_OmitsCommandLine()
    {
        var feedback = new ReviewFeedback(
            Rationale: "gate: build failed",
            ChecksFailed: new[] { "build" },
            ReworkRoundNumber: 1,
            GateFailedChecks: new[]
            {
                new CheckResult("build", Passed: false, ExitCode: 1,
                    StdoutTail: "broken", StderrTail: "",
                    Elapsed: TimeSpan.FromSeconds(1))
            });

        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: feedback);

        Assert.DoesNotContain("Command:", brief.Context["review_feedback_section"]);
    }

    [Fact]
    public void Build_ReviewFeedbackWithFailedCheckDetails_RendersDetailsAndInstruction()
    {
        var feedback = new ReviewFeedback(
            Rationale: "reviewer: the linter rejects the new file's import order",
            ChecksFailed: new[] { "lint" },
            ReworkRoundNumber: 1,
            FailedCheckDetails: new[]
            {
                new CheckResult("lint", Passed: false, ExitCode: 1,
                    StdoutTail: "file.swift:2:8 sorted_imports", StderrTail: "",
                    Elapsed: TimeSpan.FromSeconds(2),
                    CommandLine: "swiftlint --strict --no-cache")
            });

        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: feedback);

        var section = brief.Context["review_feedback_section"];
        // Review-originated rework keeps its heading and rationale...
        Assert.Contains("## Rework round 1 - reviewer feedback", section);
        Assert.Contains("the linter rejects the new file's import order", section);
        // ...and now carries the failing check's own evidence plus the re-run contract.
        Assert.Contains("### Failed check: lint (exit 1)", section);
        Assert.Contains("file.swift:2:8 sorted_imports", section);
        Assert.Contains("Command: `swiftlint --strict --no-cache`", section);
        Assert.Contains("re-run each failed check's exact command", section);
    }

    [Fact]
    public void Build_ReviewFeedbackWithoutFailedCheckDetails_NoDetailBlocks()
    {
        var feedback = new ReviewFeedback(
            Rationale: "reviewer says fix it",
            ChecksFailed: new[] { "tests" },
            ReworkRoundNumber: 1);

        var brief = ImplementBriefBuilder.Build("claude-code", MinimalTicket(), MinimalRepo(), Branch, Worktree, reviewFeedback: feedback);

        var section = brief.Context["review_feedback_section"];
        Assert.DoesNotContain("### Failed check:", section);
        Assert.DoesNotContain("re-run each failed check's exact command", section);
    }
}
