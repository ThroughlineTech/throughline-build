# ThroughlineBuild.Workers.ClaudeCode - vendor worker (template)

`ClaudeCodeAgent : IWorkerAgent` (`Name => "claude-code"`). Spawns
`claude --print --output-format stream-json` with the brief on stdin, parses
the NDJSON stream, and emits a one-line progress digest
(`ClaudeCodeProgressDigester`).

WORKER_RESULT + fenced blocks are parsed from the FULL assistant transcript
reconstructed from the stream (`TryExtractAssistantTranscript`), not from the
terminal envelope's `result` field alone - that field carries only the final
assistant message, and models that split output across messages (Fable) would
otherwise lose blocks emitted in earlier messages. `result` remains the
fallback for the legacy single-blob shape and the source of usage/cost.

This is the reference pattern for the other three vendors
(`Workers.Codex`, `Workers.Gemini`, `Workers.Copilot`). To add or change a vendor:
1. Implement `IWorkerAgent` (unique `Name`, spawn the CLI, parse its output to a
   `WorkerResult` via `Workers.Common`, optional `IWorkerProgressDigester`).
2. Register it in the name->IWorkerAgent dictionary wired in
   `ThroughlineBuild.Cli/Program.cs` (resolved by `WorkerAgentFactory`).

CLI invocation shape differs per vendor: ClaudeCode = brief on stdin;
Codex = brief as positional prompt (`codex exec`); Copilot = `-p "<brief>"`
with per-tool `--allow-tool` flags; Gemini = JSON DTO parsing. Copilot has no
digester. Size->model mapping comes from config (`[workers.<name>.sizes]`).

AOT regression coverage is concentrated here - keep new parsing AOT-safe.
