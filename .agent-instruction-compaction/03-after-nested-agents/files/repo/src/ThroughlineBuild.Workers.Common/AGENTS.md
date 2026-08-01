# ThroughlineBuild.Workers.Common - shared worker contract

Every vendor worker depends on this project.

`WorkerResultParser` reverse-scans for `WORKER_RESULT`, so the last envelope
wins; the payload is the first complete JSON value after the marker, with
trailing narration ignored. Metadata is AOT-safe
`Dictionary<string, JsonElement>`. A fenced-block pre-pass captures
`<<<NAME_START` / `<<<NAME_END` blocks up to the last marker with duplicate
names last-wins; `FencedBlockResolver.TryResolveRef` resolves metadata `*_ref`.
Batch envelopes share the file.

Specs:
[../../docs/build-worker-result-envelope.md](../../docs/build-worker-result-envelope.md),
[../../docs/op-docs/examples/op-27-worker-result-fenced-payloads.md](../../docs/op-docs/examples/op-27-worker-result-fenced-payloads.md).

Also here: `CompletionClaimParser`, `ProviderErrorClassifier` for transient
vendor quota/rate/auth failures, `ProcessStreamEncoding` for UTF-8 child
stdout/stderr, `WorkerDiagnostics`, and hand-rolled AOT-safe `MarkdownRenderer`
for Plane. Do not swap in a reflection-based Markdown library. AOT regression
coverage lives here and in `Workers.ClaudeCode.Tests`.
