using System.Text.Json;
using ClaudeInteractiveProbe;
using Xunit;

namespace ThroughlineBuild.Workers.ClaudeCode.Tests;

public class ClaudeInteractiveProbeContractTests
{
    [Fact]
    public void InspectStopPayload_RecordsRequiredFieldPresence()
    {
        const string json = """
            {
              "session_id": "session",
              "cwd": "C:\\repo",
              "transcript_path": "C:\\transcript.jsonl",
              "last_assistant_message": "sentinel",
              "stop_hook_active": false
            }
            """;

        var shape = ProbeContract.InspectStopPayload(json);

        Assert.Equal(new StopPayloadShape(true, true, true, true, true), shape);
    }

    [Fact]
    public void BuildSettingsJson_ProducesOneStopCommandHook()
    {
        var json = ProbeContract.BuildSettingsJson("probe capture-hook payload.json");
        using var document = JsonDocument.Parse(json);

        var hook = document.RootElement.GetProperty("hooks").GetProperty("Stop")[0]
            .GetProperty("hooks")[0];
        Assert.Equal("command", hook.GetProperty("type").GetString());
        Assert.Equal("probe capture-hook payload.json", hook.GetProperty("command").GetString());
        Assert.Equal(30, hook.GetProperty("timeout").GetInt32());
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("C:\\with space\\probe.exe", "\"C:\\with space\\probe.exe\"")]
    public void QuoteCommandArgument_QuotesOnlyWhenRequired(string input, string expected)
    {
        Assert.Equal(expected, ProbeContract.QuoteCommandArgument(input));
    }

    [Fact]
    public void NormalizeHookPath_UsesBashSafeSeparators()
    {
        Assert.Equal("C:/probe/output.json", ProbeContract.NormalizeHookPath("C:\\probe\\output.json"));
    }
}
