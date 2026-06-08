using System.Text.Json;
using System.Text.Json.Serialization;
using ThroughlineBuild.Contracts.Models;

namespace ThroughlineBuild.Workers.Common;

// DTO for deserializing the COMPLETION_CLAIM JSON block emitted by worker agents.
// File-scoped so it can be registered with WorkersCommonJsonContext for source-gen.
internal sealed class CompletionClaimDto
{
    [JsonPropertyName("provides")]
    public List<string>? Provides { get; set; }
    [JsonPropertyName("consumes")]
    public List<string>? Consumes { get; set; }
    [JsonPropertyName("ac_bindings")]
    public List<AcBindingDto>? AcBindings { get; set; }
    [JsonPropertyName("tests_added")]
    public List<string>? TestsAdded { get; set; }
}

internal sealed class AcBindingDto
{
    [JsonPropertyName("ac_ref")]
    public string? AcRef { get; set; }
    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter<VerifierKind>))]
    public VerifierKind? Kind { get; set; }
}

// Parses the COMPLETION_CLAIM fenced block emitted by implement workers.
internal static class CompletionClaimParser
{
    // Parses a COMPLETION_CLAIM JSON string into a typed CompletionClaim.
    // All four arrays (provides, consumes, ac_bindings, tests_added) are required
    // (but may be empty). Returns false with a diagnostic message on any failure.
    // AOT-safe: uses source-gen WorkersCommonJsonContext.
    internal static bool TryParse(
        string json,
        out CompletionClaim? claim,
        out string? errorMessage)
    {
        claim = null;
        errorMessage = null;
        try
        {
            var dto = JsonSerializer.Deserialize(json, WorkersCommonJsonContext.Default.CompletionClaimDto);
            if (dto is null)
            {
                errorMessage = "COMPLETION_CLAIM JSON deserialized to null";
                return false;
            }
            if (dto.Provides is null || dto.Consumes is null || dto.AcBindings is null || dto.TestsAdded is null)
            {
                errorMessage = "COMPLETION_CLAIM missing one or more required arrays: provides, consumes, ac_bindings, tests_added";
                return false;
            }
            var bindings = new List<AcBinding>(dto.AcBindings.Count);
            for (int i = 0; i < dto.AcBindings.Count; i++)
            {
                var b = dto.AcBindings[i];
                if (string.IsNullOrWhiteSpace(b.AcRef))
                {
                    errorMessage = $"COMPLETION_CLAIM ac_bindings[{i}].ac_ref is required and must be non-empty";
                    return false;
                }
                if (b.Kind is null)
                {
                    errorMessage = $"COMPLETION_CLAIM ac_bindings[{i}].kind is required";
                    return false;
                }
                bindings.Add(new AcBinding(b.AcRef, b.Kind.Value));
            }
            claim = new CompletionClaim(
                Provides: dto.Provides.AsReadOnly(),
                Consumes: dto.Consumes.AsReadOnly(),
                AcBindings: bindings.AsReadOnly(),
                TestsAdded: dto.TestsAdded.AsReadOnly());
            return true;
        }
        catch (JsonException ex)
        {
            errorMessage = $"COMPLETION_CLAIM JSON parse error: {ex.Message}";
            return false;
        }
        catch (NotSupportedException ex)
        {
            errorMessage = $"COMPLETION_CLAIM JSON deserialization error (AOT): {ex.Message}";
            return false;
        }
    }
}
