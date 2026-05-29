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
}
