using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace ThroughlineBuild.EventLog.Tests;

public class AnalyzeEventLogIntegrationTests
{
    // Compute the solution root by walking 5 parent directories up from the test binary directory.
    // Test binary: tests/ThroughlineBuild.EventLog.Tests/bin/Debug/net8.0/
    // Parent 1: bin/Debug/
    // Parent 2: bin/
    // Parent 3: ThroughlineBuild.EventLog.Tests/
    // Parent 4: tests/
    // Parent 5: solution root
    private static string GetSolutionRoot()
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        for (int i = 0; i < 5; i++)
            dir = Directory.GetParent(dir)!.FullName;
        return dir;
    }

    [Fact]
    public async Task TicketSubsumedEvent_AppearsInChainSummaryOutput()
    {
        var solutionRoot = GetSolutionRoot();
        var toolPath = Path.Combine(solutionRoot, "src", "tools", "analyze-event-log.cs");

        var tempFile = Path.Combine(Path.GetTempPath(), $"analyze-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            // Kind=9 TicketSubsumed must appear before Kind=7 ChainEnd in the file
            // (documents the cause-then-terminal ordering requirement)
            var lines = new[]
            {
                """{"SessionId":"test-session","Timestamp":"2026-05-29T00:00:00+00:00","Kind":9,"TicketId":"TLB-34","Phase":4,"Data":{"ticket_id":"TLB-34","subsumed_by_commit":"abc123def","files":["src/Foo.cs"],"rationale":"already done"}}""",
                """{"SessionId":"test-session","Timestamp":"2026-05-29T00:00:01+00:00","Kind":7,"TicketId":"TLB-34","Phase":4,"Data":{"outcome":"RatifiedObsolete","phases_run":2,"rework_rounds":0,"total_duration_ms":5000}}"""
            };
            await File.WriteAllLinesAsync(tempFile, lines);

            var psi = new ProcessStartInfo("dotnet");
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add(toolPath);
            psi.ArgumentList.Add(tempFile);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;

            using var proc = Process.Start(psi)!;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            Assert.Contains("Subsumed:", output);
            Assert.Contains("abc123def", output);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task OpenAiLlmCall_UsesCachedInputPricingAndShowsReasoningTokens()
    {
        var solutionRoot = GetSolutionRoot();
        var toolPath = Path.Combine(solutionRoot, "src", "tools", "analyze-event-log.cs");

        var tempFile = Path.Combine(Path.GetTempPath(), $"analyze-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            var lines = new[]
            {
                """{"SessionId":"test-session","Timestamp":"2026-06-03T00:00:00+00:00","Kind":1,"TicketId":"TLB-34","Phase":1,"Data":{"model":"gpt-5.4","vendor":"openai","input_tokens":1000000,"output_tokens":100000,"cache_read_tokens":250000,"cache_create_tokens":0,"cached_input_tokens":250000,"reasoning_output_tokens":12000,"wall_clock_ms":1000}}"""
            };
            await File.WriteAllLinesAsync(tempFile, lines);

            var psi = new ProcessStartInfo("dotnet");
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add(toolPath);
            psi.ArgumentList.Add(tempFile);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;

            using var proc = Process.Start(psi)!;
            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            Assert.Equal(0, proc.ExitCode);
            Assert.Contains("reasoning", output);
            Assert.Contains("12,000", output);
            Assert.Contains("$3.4375", output);
            Assert.DoesNotContain("cost is partial", output);
            Assert.True(string.IsNullOrWhiteSpace(error), error);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task EventSuppliedCostAndVerifierRework_AreUsedInChainSummary()
    {
        var solutionRoot = GetSolutionRoot();
        var toolPath = Path.Combine(solutionRoot, "src", "tools", "analyze-event-log.cs");

        var tempFile = Path.Combine(Path.GetTempPath(), $"analyze-test-{Guid.NewGuid():N}.jsonl");
        try
        {
            var lines = new[]
            {
                """{"SessionId":"test-session","Timestamp":"2026-06-03T00:00:00+00:00","Kind":1,"TicketId":"TLB-419","Phase":1,"Data":{"model":"unpriced-model","vendor":"anthropic","input_tokens":1000000,"output_tokens":1000000,"cache_read_tokens":1000000,"cache_create_tokens":1000000,"wall_clock_ms":1000,"cost_usd":11.7001}}""",
                """{"SessionId":"test-session","Timestamp":"2026-06-03T00:00:01+00:00","Kind":3,"TicketId":"TLB-419","Phase":2,"Data":{"kind":"Rework","rationale":"needs one fix"}}""",
                """{"SessionId":"test-session","Timestamp":"2026-06-03T00:00:02+00:00","Kind":7,"TicketId":"TLB-419","Phase":4,"Data":{"outcome":"Pass","phases_run":4,"rework_rounds":0,"total_duration_ms":5000}}"""
            };
            await File.WriteAllLinesAsync(tempFile, lines);

            var psi = new ProcessStartInfo("dotnet");
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add(toolPath);
            psi.ArgumentList.Add(tempFile);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;

            using var proc = Process.Start(psi)!;
            var output = await proc.StandardOutput.ReadToEndAsync();
            var error = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            Assert.Equal(0, proc.ExitCode);
            Assert.Contains("Rework rounds:  1", output);
            Assert.Contains("TLB-419: 1", output);
            Assert.Contains("$11.7001", output);
            Assert.DoesNotContain("cost is partial", output);
            Assert.True(string.IsNullOrWhiteSpace(error), error);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
