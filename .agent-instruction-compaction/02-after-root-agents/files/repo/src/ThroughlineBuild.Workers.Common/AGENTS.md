# ThroughlineBuild.Workers.Common - shared worker contract

Code every vendor worker (ClaudeCode/Codex/Gemini/Copilot) depends on.

`WorkerResultParser` extracts the `WORKER_RESULT` JSON envelope by REVERSE
scan, so the LAST envelope wins; the payload is the first complete JSON value
after the marker (trailing narration ignored). Metadata is an AOT-safe
`Dictionary<string, JsonElement>`. A fenced-block pre-pass captures
`<<<NAME_START` / `<<<NAME_END` blocks up to the LAST marker (duplicate names
last-wins); `FencedBlockResolver.TryResolveRef` resolves a metadata `*_ref` to
its block body. Batch envelopes (`BatchWorkerResultDto`) share the file.
Protocol spec: [../../docs/build-worker-result-envelope.md](../../docs/build-worker-result-envelope.md)
and [../../docs/op-docs/examples/op-27-worker-result-fenced-payloads.md](../../docs/op-docs/examples/op-27-worker-result-fenced-payloads.md).

Also here: `CompletionClaimParser` (gate-phase completion claims),
`ProviderErrorClassifier` (pattern-matches vendor quota/rate/auth failures
into one transient classification), `ProcessStreamEncoding` (pins child
stdout/stderr to UTF-8 - without it .NET decodes with the OEM code page),
`WorkerDiagnostics` (stderr diagnostic sink; tests mute it via a
ModuleInitializer), and `MarkdownRenderer` (hand-rolled AOT-safe md->HTML for
Plane; do not swap in a reflection-based lib). AOT regression coverage lives
in this project's tests and `Workers.ClaudeCode.Tests`.
