# Worker agent adapter mapping

Throughline Build supports four worker CLIs behind one
[`IWorkerAgent`](../src/ThroughlineBuild.Contracts/IWorkerAgent.cs) contract. A
phase supplies a provider-neutral brief, working directory, timeout, model-size
signal, optional tool allowlist, environment overrides, and diagnostic sinks.
The adapter owns the provider-specific process invocation and converts the
response into a common `WorkerResult`. The shared wire format is documented in
the [WORKER_RESULT envelope specification](worker-result-envelope.md).

Configured agent names are exact and case-sensitive:

| Config name | Executable and request shape | Result handling | Progress digest |
|---|---|---|---|
| `claude-code` | `claude`; the default `interactive-hook` transport runs an interactive terminal session and reads Claude's persisted transcript. The rollback `print` transport sends the brief on stdin and requests stream JSON. | Extracts assistant output and parses the shared `WORKER_RESULT` block. | Yes |
| `codex` | `codex exec --json -`; sends the brief on stdin. | Extracts agent messages from JSONL, then parses the shared `WORKER_RESULT` block. | Yes |
| `gemini` | `gemini -p <brief> --output-format json`; sends the brief as the prompt argument. | Reads the JSON envelope's `response`, then parses the shared `WORKER_RESULT` block; raw stdout is a fallback. | Yes |
| `copilot` | `copilot -p <brief> -s --no-ask-user`; sends the brief as the prompt argument. | Parses the shared `WORKER_RESULT` block from plain-text stdout. | No |

Unknown names fail configuration with the registered names listed in the error.
The construction mapping lives in
[`WorkerAgentBuilder`](../src/ThroughlineBuild.Cli/WorkerAgentBuilder.cs); the
factory itself is a provider-independent, ordinal string registry.

## Tool restrictions

`WorkerOptions.AllowedTools` is optional, but the provider CLIs do not expose
equivalent restriction mechanisms:

| Adapter | Mapping of `AllowedTools` |
|---|---|
| Claude Code | A comma-separated `--allowedTools` value. The adapter also disallows nested-agent tools so a one-shot worker cannot yield to a subagent. |
| Codex | Not mapped. Codex has no per-tool allowlist flag used by this adapter; read-only review is enforced by the brief and a post-review git-state guard. |
| Gemini | Not mapped. |
| Copilot | One `--allow-tool` argument per supplied name. |

The default review allowlist is `Read`, `Grep`, and `Glob`. Those are Claude
Code tool names, not a provider-neutral vocabulary. Passing them to another
adapter does not translate them into semantically equivalent provider tools.
Any new cross-provider restriction should therefore be added as an explicit
capability or policy in the shared contract, with deliberate mappings in each
adapter, rather than by expanding a table of guessed vendor aliases.

Claude Code also interprets `LeanPlanning` in the adapter by removing its
planning tools from the allowed set. Other adapters currently do not map that
hint.

## Permission and authentication behavior

Each adapter owns unattended-execution flags because the provider CLIs use
different approval models:

- Claude Code maps its permission setting in both interactive and print
  transports and removes `ANTHROPIC_API_KEY` from the child environment before
  applying explicit worker environment overrides.
- Codex can add its approvals-and-sandbox bypass flag. It removes
  `CODEX_API_KEY` and `OPENAI_API_KEY` before applying explicit overrides.
- Gemini can add `--yolo`. It removes `GEMINI_API_KEY` and `GOOGLE_API_KEY`
  before applying explicit overrides.
- Copilot always runs with `--no-ask-user`; supplied tool restrictions become
  `--allow-tool` arguments.

The environment removals make the configured CLI session the default
authentication path. An explicit `WorkerOptions.EnvironmentVariables` entry is
applied afterward and therefore wins.

## Why the shared contract stays small

[`IWorkerAgent`](../src/ThroughlineBuild.Contracts/IWorkerAgent.cs) standardizes
only what the workflow needs:

- a stable adapter name and optional progress digester;
- asynchronous execution of a brief in a working directory;
- timeout, cancellation, streaming, debug capture, and transcript metadata;
- a provider-neutral size signal; and
- a normalized `WorkerResult`.

Invocation syntax, authentication environment, permission flags, model-name
normalization, streaming envelopes, and usage metadata remain adapter
responsibilities. All adapters ultimately use the shared `WorkerResultParser`,
so phases consume the same `WORKER_RESULT` protocol without depending on a
vendor's wire format. The optional structured debug transcript is currently a
Claude Code-specific diagnostic; see the
[debug transcript reference](debug-transcript-format.md).

When adding an adapter, implement `IWorkerAgent`, use the shared parser, register
the exact config name in `WorkerAgentBuilder`, and cover the common good/error
result fixtures in the worker contract tests.
