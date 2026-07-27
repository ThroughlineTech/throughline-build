using System.Text.Json;
using ThroughlineBuild.Contracts;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.Phases;
using Xunit;

namespace ThroughlineBuild.Phases.Tests;

// Covers the per-rework-round --debug side channel: the record analysis uses to split a
// rework into a design miss (front-loadable) vs a hygiene slip (the gate's job). Parses with
// JsonDocument so the assertions pin the actual JSON shape, not a string match.
public class ReworkRoundManifestTests
{
    private static JsonElement Parse(string json)
    {
        using var d = JsonDocument.Parse(json);
        return d.RootElement.Clone();
    }

    [Fact]
    public void BuildJson_ReviewTriggeredRework_LabelsTriggerReviewAndCarriesPayload()
    {
        var feedback = new ReviewFeedback(
            Rationale: "The discriminated union is missing the 'archived' case.",
            ChecksFailed: new[] { "design" },
            ReworkRoundNumber: 1);

        var json = ReworkRoundManifest.BuildJson(round: 1, feedback, shaBefore: "aaa111", shaAfter: "bbb222");
        var root = Parse(json);

        Assert.Equal(1, root.GetProperty("round").GetInt32());
        Assert.Equal("review", root.GetProperty("trigger").GetString());
        Assert.Equal("The discriminated union is missing the 'archived' case.", root.GetProperty("rationale").GetString());
        Assert.Equal(new[] { "design" },
            root.GetProperty("checks_failed").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("aaa111", root.GetProperty("sha_before").GetString());
        Assert.Equal("bbb222", root.GetProperty("sha_after").GetString());
        Assert.Empty(root.GetProperty("gate_failed_checks").EnumerateArray());
    }

    [Fact]
    public void BuildJson_GateTriggeredRework_LabelsTriggerGateAndCarriesCheckTailsVerbatim()
    {
        var failedCheck = new CheckResult(
            Name: "test", Passed: false, ExitCode: 1,
            StdoutTail: "FAILED tests/Foo.cs::bar",
            StderrTail: "Assert.Equal() Failure",
            Elapsed: TimeSpan.FromSeconds(3),
            Role: CheckRole.Gating);

        var feedback = new ReviewFeedback(
            Rationale: "gate: gating checks failed",
            ChecksFailed: new[] { "test" },
            ReworkRoundNumber: 2,
            GateFailedChecks: new[] { failedCheck });

        var json = ReworkRoundManifest.BuildJson(round: 2, feedback, shaBefore: "c0ffee", shaAfter: "decaf0");
        var root = Parse(json);

        Assert.Equal("gate", root.GetProperty("trigger").GetString());
        var checks = root.GetProperty("gate_failed_checks");
        var check = Assert.Single(checks.EnumerateArray().ToList());
        Assert.Equal("test", check.GetProperty("name").GetString());
        Assert.Equal(1, check.GetProperty("exit_code").GetInt32());
        Assert.Equal("FAILED tests/Foo.cs::bar", check.GetProperty("stdout_tail").GetString());
        Assert.Equal("Assert.Equal() Failure", check.GetProperty("stderr_tail").GetString());
    }

    [Fact]
    public void BuildJson_NullShas_SerializeAsNull()
    {
        var feedback = new ReviewFeedback("r", Array.Empty<string>(), 1);
        var root = Parse(ReworkRoundManifest.BuildJson(1, feedback, shaBefore: null, shaAfter: null));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sha_before").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sha_after").ValueKind);
    }

    [Fact]
    public void Write_NullCaptureDir_IsNoOp()
    {
        // Must not throw and must not create anything.
        var feedback = new ReviewFeedback("r", Array.Empty<string>(), 1);
        ReworkRoundManifest.Write(null, 1, feedback, null, null);
    }

    [Fact]
    public void Write_CreatesReworkRoundJsonInCaptureDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tlb-rework-" + Guid.NewGuid().ToString("N"));
        try
        {
            var feedback = new ReviewFeedback("nope", new[] { "build" }, 1);
            ReworkRoundManifest.Write(dir, 1, feedback, "before", "after");

            var path = Path.Combine(dir, ReworkRoundManifest.FileName);
            Assert.True(File.Exists(path));
            var root = Parse(File.ReadAllText(path));
            Assert.Equal("after", root.GetProperty("sha_after").GetString());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
