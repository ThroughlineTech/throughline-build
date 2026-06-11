# ThroughlineBuild.Workers.ClaudeCode - vendor worker (template)

`ClaudeCodeAgent : IWorkerAgent` (`Name => "claude-code"`). Spawns
`claude --print --verbose --output-format stream-json` with the brief on
stdin, parses the NDJSON stream, and emits a one-line progress digest
(`ClaudeCodeProgressDigester`).

WORKER_RESULT + fenced blocks are parsed from the FULL assistant transcript
reconstructed from the stream (`TryExtractAssistantTranscript`), not from the
terminal envelope's `result` field alone - models that split output across
messages (Fable, especially) would otherwise lose earlier blocks. `result`
stays as legacy fallback and the source of usage/cost.
`ClaudeCodeModelValidator` rejects bad `[workers.claude-code.sizes]` values at
config load: only tier aliases haiku/sonnet/opus or a full claude-* slug -
`model = "fable"` must be `"claude-fable-5"`. `WorkerTranscriptWriter` emits a
stable JSONL transcript under --debug (pure observation, post-exit only).

Reference pattern for the other vendors. To add/change one: implement
`IWorkerAgent` (parse via `Workers.Common`), then add the name -> class arm in
`ThroughlineBuild.Cli/WorkerAgentBuilder.cs` (NOT Program.cs). Invocation
shapes differ: ClaudeCode + Codex (`codex exec --json -`) take the brief on
stdin; Copilot = `-p "<brief>"` + per-tool `--allow-tool`; Gemini = JSON DTOs.
Copilot has no digester. Keep new parsing AOT-safe.
