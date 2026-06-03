# ThroughlineBuild.Workers.Common - shared worker contract

Code every vendor worker (ClaudeCode/Codex/Gemini/Copilot) depends on.

`WorkerResultParser` extracts the `WORKER_RESULT` JSON envelope from worker
stdout by REVERSE scan, so the last envelope wins. Metadata is an AOT-safe
`Dictionary<string, JsonElement>`. A fenced-block pre-pass captures
`<<<NAME_START` / `<<<NAME_END` payload blocks emitted before the envelope;
`FencedBlockResolver.TryResolveRef` resolves a metadata `*_ref` field to its
block body. Protocol spec: [../../docs/op-docs/complete/op-27-worker-result-fenced-payloads.md](../../docs/op-docs/complete/op-27-worker-result-fenced-payloads.md)
and [../../docs/worker-result-envelope.md](../../docs/worker-result-envelope.md).

`MarkdownRenderer` is a hand-rolled, AOT-safe CommonMark-subset md->HTML
renderer used to turn resolved block bodies into Plane HTML. Do not swap in a
reflection-based markdown lib - it would break AOT.

AOT regression coverage for the parser is concentrated in this project's tests
and `Workers.ClaudeCode.Tests`.
