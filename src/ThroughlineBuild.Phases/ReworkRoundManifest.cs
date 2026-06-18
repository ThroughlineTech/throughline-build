using System.Text;
using System.Text.Json;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Phases;

/// <summary>
/// Writes a per-rework-round side-channel record under the round's --debug capture directory
/// (<c>rework-round.json</c>). A rework round is an implement re-dispatch driven by a prior
/// failure; analysis wants to classify each one as a design miss (front-loadable into the
/// op-doc) vs a hygiene slip (the gate's job). To do that mechanically it needs three things
/// the worker's own stream never carries because they live in the orchestrator: which
/// gate/check or verifier verdict TRIGGERED the round, the failure payload VERBATIM (the same
/// text fed back into the rework brief), and the commit sha BEFORE and AFTER the round.
///
/// Pure observation: this never touches the worker's prompt or behavior. No-op when captureDir
/// is null (debug off); every failure is swallowed so debug capture cannot change phase flow.
/// </summary>
internal static class ReworkRoundManifest
{
    internal const string FileName = "rework-round.json";

    public static void Write(
        string? captureDir,
        int round,
        ReviewFeedback feedback,
        string? shaBefore,
        string? shaAfter)
    {
        if (captureDir is null)
            return;

        try
        {
            Directory.CreateDirectory(captureDir);
            var json = BuildJson(round, feedback, shaBefore, shaAfter);
            File.WriteAllText(Path.Combine(captureDir, FileName), json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Best-effort: a manifest-write failure must never alter the rework loop.
        }
    }

    // Exposed for unit testing without filesystem IO.
    internal static string BuildJson(int round, ReviewFeedback feedback, string? shaBefore, string? shaAfter)
    {
        // A gate hard-fail carries the failed CheckResults (with stdout/stderr tails); a reviewer
        // Rework verdict does not. That presence/absence is exactly the design-miss vs hygiene-slip
        // split the analysis is after, so it becomes the trigger label.
        bool gateTriggered = feedback.GateFailedChecks is { Count: > 0 };

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("round", round);
            w.WriteString("trigger", gateTriggered ? "gate" : "review");
            w.WriteString("rationale", feedback.Rationale ?? "");

            w.WritePropertyName("checks_failed");
            w.WriteStartArray();
            foreach (var c in feedback.ChecksFailed)
                w.WriteStringValue(c);
            w.WriteEndArray();

            WriteOptionalString(w, "sha_before", shaBefore);
            WriteOptionalString(w, "sha_after", shaAfter);

            w.WritePropertyName("gate_failed_checks");
            w.WriteStartArray();
            if (feedback.GateFailedChecks is { Count: > 0 } checks)
            {
                foreach (var check in checks)
                {
                    w.WriteStartObject();
                    w.WriteString("name", check.Name);
                    w.WriteNumber("exit_code", check.ExitCode);
                    w.WriteString("stdout_tail", check.StdoutTail ?? "");
                    w.WriteString("stderr_tail", check.StderrTail ?? "");
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteOptionalString(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }
}
