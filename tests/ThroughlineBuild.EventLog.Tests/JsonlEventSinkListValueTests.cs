using System.Text.Json;
using ThroughlineBuild.Contracts.Models;
using ThroughlineBuild.EventLog;
using Xunit;

namespace ThroughlineBuild.EventLog.Tests;

public class JsonlEventSinkListValueTests
{
    [Fact]
    public async Task EmitAsync_WithListStringValues_DoesNotThrowAndSerializesCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var options = new EventLogOptions { BaseDirectory = tempDir, SessionId = "test-list-values" };

        try
        {
            var sink = new JsonlEventSink(options);
            try
            {
                var data = new Dictionary<string, object>
                {
                    ["list_of_string"] = new List<string> { "a", "b" },
                    ["ireadonly_list"] = (IReadOnlyList<string>)new string[] { "x", "y" },
                    ["string_array"] = new string[] { "p", "q" }
                };

                var evt = new WorkflowEvent(
                    SessionId: "test-list-values",
                    Timestamp: DateTimeOffset.UtcNow,
                    Kind: EventKind.StateTransition,
                    TicketId: "TLB-130",
                    Phase: Phase.Implement,
                    Data: data
                );

                await sink.EmitAsync(evt, CancellationToken.None);
                await sink.FlushAsync(CancellationToken.None);
            }
            finally
            {
                await sink.DisposeAsync();
            }

            var filePath = Path.Combine(tempDir, "test-list-values.jsonl");
            Assert.True(File.Exists(filePath));

            var content = File.ReadAllText(filePath);
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Single(lines);

            var json = JsonDocument.Parse(lines[0]);

            // Get the Data property - it's serialized as "Data" (capitalized)
            Assert.True(json.RootElement.TryGetProperty("Data", out var dataElement),
                "Expected Data property in serialized JSON");

            // Verify list_of_string
            var listOfString = dataElement.GetProperty("list_of_string");
            Assert.Equal(2, listOfString.GetArrayLength());
            var listArray = listOfString.EnumerateArray().ToList();
            Assert.Equal("a", listArray[0].GetString());
            Assert.Equal("b", listArray[1].GetString());

            // Verify ireadonly_list
            var ireadOnlyList = dataElement.GetProperty("ireadonly_list");
            Assert.Equal(2, ireadOnlyList.GetArrayLength());
            var ireadOnlyArray = ireadOnlyList.EnumerateArray().ToList();
            Assert.Equal("x", ireadOnlyArray[0].GetString());
            Assert.Equal("y", ireadOnlyArray[1].GetString());

            // Verify string_array
            var stringArray = dataElement.GetProperty("string_array");
            Assert.Equal(2, stringArray.GetArrayLength());
            var strArray = stringArray.EnumerateArray().ToList();
            Assert.Equal("p", strArray[0].GetString());
            Assert.Equal("q", strArray[1].GetString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task EmitAsync_WithListLongValues_RoundTripsForContextAttribution()
    {
        // AOT regression guard for the List<long> source-gen registration: without it, serializing
        // a boxed List<long> inside the event Data throws once reflection is disabled below.
        AppContext.SetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", false);

        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var options = new EventLogOptions { BaseDirectory = tempDir, SessionId = "test-list-long-values" };

        try
        {
            var sink = new JsonlEventSink(options);
            try
            {
                var data = new Dictionary<string, object>
                {
                    ["kind"] = "context_attribution",
                    ["cache_read_series"] = new List<long> { 35000, 48000, 60000 },
                    ["total_cache_read"] = (long)143000,
                    ["slope_ratio"] = 1.7d,
                    ["turns"] = 3
                };

                var evt = new WorkflowEvent(
                    SessionId: "test-list-long-values",
                    Timestamp: DateTimeOffset.UtcNow,
                    Kind: EventKind.CostLedger,
                    TicketId: "TLB-130",
                    Phase: Phase.Implement,
                    Data: data
                );

                await sink.EmitAsync(evt, CancellationToken.None);
                await sink.FlushAsync(CancellationToken.None);
            }
            finally
            {
                await sink.DisposeAsync();
            }

            var filePath = Path.Combine(tempDir, "test-list-long-values.jsonl");
            Assert.True(File.Exists(filePath));

            var content = File.ReadAllText(filePath);
            var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Single(lines);

            var json = JsonDocument.Parse(lines[0]);

            Assert.True(json.RootElement.TryGetProperty("Data", out var dataElement),
                "Expected Data property in serialized JSON");

            var cacheReadSeries = dataElement.GetProperty("cache_read_series");
            Assert.Equal(3, cacheReadSeries.GetArrayLength());
            var seriesArray = cacheReadSeries.EnumerateArray().ToList();
            Assert.Equal(35000, seriesArray[0].GetInt64());
            Assert.Equal(48000, seriesArray[1].GetInt64());
            Assert.Equal(60000, seriesArray[2].GetInt64());

            Assert.Equal(143000, dataElement.GetProperty("total_cache_read").GetInt64());
            Assert.Equal("context_attribution", dataElement.GetProperty("kind").GetString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
