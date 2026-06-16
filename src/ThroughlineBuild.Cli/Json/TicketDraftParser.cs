using System.Text.Json;

namespace ThroughlineBuild.Cli.Json;

/// <summary>
/// Parses the strict JSON draft consumed by <c>build new - --json</c>. Deserialization
/// rejects unknown fields (TicketDraft is annotated Disallow), and malformed JSON or an
/// unknown field surfaces as a friendly usage message rather than a raw exception.
/// </summary>
public static class TicketDraftParser
{
    public static bool TryParse(string json, out TicketDraft? draft, out string? error)
    {
        draft = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "expected a JSON ticket draft on stdin, got empty input";
            return false;
        }

        try
        {
            draft = (TicketDraft?)JsonSerializer.Deserialize(json, CliJsonContext.Default.TicketDraft);
        }
        catch (JsonException ex)
        {
            // Covers malformed JSON and unknown/disallowed fields.
            error = $"invalid ticket draft JSON: {ex.Message}";
            return false;
        }

        if (draft is null)
        {
            error = "ticket draft JSON deserialized to null";
            return false;
        }

        return true;
    }
}
