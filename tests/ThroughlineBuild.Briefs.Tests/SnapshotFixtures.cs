using ThroughlineBuild.Briefs;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Briefs.Tests;

internal static class SnapshotFixtures
{
    public const string FixtureBranch = "ticket/tlb-snap-fixture";
    public const string FixtureWorktree = "/repo/.worktrees/ticket-tlb-snap-fixture";

    public static Ticket Ticket() => new Ticket(
        Id: "TLB-SNAP",
        Uuid: "snapshot-uuid-1",
        Title: "Snapshot fixture ticket",
        Type: "feature",
        State: TicketState.Ready,
        Size: Size.M,
        Risk: Risk.Medium,
        DescriptionHtml: "<h2>Plan</h2><p>Implement the fixture feature.</p><ul><li>Step 1</li><li>Step 2</li></ul>",
        Relations: new[] { new Relation("blocks", "TLB-100"), new Relation("blocked_by", "TLB-99") },
        Labels: Array.Empty<string>(),
        ParentId: null);

    public static RepoState Repo() => new RepoState(
        MainSha: "abc1234567890def",
        TopLevelEntries: new[] { "src", "tests", "docs", "README.md" });

    public static GitDiff Diff() => new GitDiff(
        FromRef: "origin/main",
        ToRef: "ticket/tlb-snap-fixture",
        Entries: new[]
        {
            new DiffEntry(
                Path: "src/Feature.cs",
                Kind: DiffKind.Added,
                OldPath: null,
                LinesAdded: 42,
                LinesRemoved: 0,
                PatchContent: "@@ -0,0 +1,3 @@\n+namespace Feature;\n+public class Feature {}\n+// stub"),
            new DiffEntry(
                Path: "src/Old.cs",
                Kind: DiffKind.Renamed,
                OldPath: "src/Older.cs",
                LinesAdded: 1,
                LinesRemoved: 1,
                PatchContent: null)
        });

    public static WorkerResult ImplementerResult() => new WorkerResult(
        Status: Status.Ok,
        Summary: "Fixture implementer summary.",
        FilesChanged: new[] { "src/Feature.cs", "src/Old.cs" },
        FailureReason: null,
        Metadata: new Dictionary<string, object>());

    public static IReadOnlyList<CheckResult> Checks() => new[]
    {
        new CheckResult(
            Name: "build",
            Passed: true,
            ExitCode: 0,
            StdoutTail: "Build succeeded",
            StderrTail: "",
            Elapsed: TimeSpan.FromSeconds(1.5)),
        new CheckResult(
            Name: "tests",
            Passed: false,
            ExitCode: 1,
            StdoutTail: "Some output",
            StderrTail: "Test 'X' failed: expected Y got Z",
            Elapsed: TimeSpan.FromSeconds(1.5))
    };

    public static ReviewFeedback ReworkFeedback() => new ReviewFeedback(
        Rationale: "The implementation is incomplete. Please add the missing test cases for the error handling path.",
        ChecksFailed: new[] { "tests" },
        ReworkRoundNumber: 1);
}
