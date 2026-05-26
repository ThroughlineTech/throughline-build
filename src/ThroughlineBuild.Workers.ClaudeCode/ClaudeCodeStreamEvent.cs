using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThroughlineBuild.Workers.ClaudeCode;

// Wire-format DTOs for individual NDJSON events emitted by the Claude Code CLI
// when invoked with --print --verbose --output-format stream-json.
//
// The stream is a sequence of newline-delimited JSON objects, each carrying a
// "type" discriminator. We discriminate by inspecting the "type" property via
// JsonDocument (rather than a polymorphic deserializer) because System.Text.Json
// source-gen does not support polymorphic deserialization cleanly under AOT.
//
// The terminal "result" event re-uses ClaudeCodeJsonEnvelope (declared in
// ClaudeCodeJsonEnvelope.cs) - its shape is bit-for-bit identical to the legacy
// --output-format json single-blob envelope, so envelope parsing stays unchanged.
//
// All non-result events are best-effort decoration for the digest renderer; if
// claude-code emits an unrecognized shape or a new event subtype, the digest
// formatter must skip silently rather than crash the worker dispatch.

// "system" event - emitted once at session start. Carries session_id and model.
public record ClaudeCodeSystemEvent(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("subtype")] string? Subtype,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("model")] string? Model
);

// "assistant" event - emitted once per completed assistant turn. The message.content
// array contains one or more content blocks (text, thinking, tool_use). We only
// surface tool_use blocks at default verbosity; text/thinking blocks are coalesced
// into a single "turn" line by the digest renderer.
public record ClaudeCodeAssistantEvent(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("message")] ClaudeCodeAssistantMessage? Message
);

public record ClaudeCodeAssistantMessage(
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("content")] List<ClaudeCodeContentBlock>? Content
);

// A content block inside an assistant message. The "type" field distinguishes
// "text", "thinking", and "tool_use". Tool-use blocks carry a "name" and an
// "input" object (the tool arguments); the digest renderer surfaces the tool
// name plus a truncated rendering of the first significant input value.
public record ClaudeCodeContentBlock(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("input")] JsonElement Input
);

// "user" event - emitted when the orchestrator sends a tool_result back into
// the model. Not surfaced by the digest renderer today (tool_result is implicit
// from the next assistant turn) but registered so the parser does not throw.
public record ClaudeCodeUserEvent(
    [property: JsonPropertyName("type")] string? Type
);
