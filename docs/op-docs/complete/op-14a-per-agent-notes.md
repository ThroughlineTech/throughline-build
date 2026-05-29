# Agent CLI mapping and tool-name reference (Brief 14)

Research input for op-14 Brief 13 (per-agent template variants) and for each future agent op-doc (Codex / Copilot / Gemini). Maps each CLI's headless behavior and tool vocabulary to the `ClaudeCodeAgent` / `ClaudeCodeOptions` surface so a `Workers.<Agent>` assembly can be written mechanically.

Sourced from public docs (March-April 2026) plus the existing claude-config port under `copilot-prompts/` and `plane-ticket-workflow/`. CLIs move monthly and docs lag real behavior, so every line tagged **VERIFY** must be confirmed against the installed binary before the agent's op-doc relies on it. Lead question per agent is the same: can it be told to emit our `WORKER_RESULT` fenced block and have it survive to where `WorkerResultParser` can read it.

## The contract each agent must satisfy

The foundation assumes, per `IWorkerAgent` / `ClaudeCodeAgent`:
1. A non-interactive invocation that runs the full tool loop and exits.
2. A way to deliver the brief (Claude Code uses stdin).
3. Output from which the terminal result is recoverable, with a `WORKER_RESULT` JSON block the agent's model is instructed (by the brief template) to emit, scannable by `WorkerResultParser`.
4. A model-selection knob (Claude Code: `--model`) fed by the sizes map.
5. An auth posture that uses the subscription/seat rather than per-token billing where possible (Claude Code strips `ANTHROPIC_API_KEY`).
6. Token/usage data for the event log (`llm_usage`: model, vendor, tokens; cost optional).
7. Optionally a stream to digest for live progress (`IWorkerProgressDigester`, nullable).

## Codex CLI (OpenAI)

Strong fit. Closest in shape to Claude Code.

- Non-interactive: `codex exec "<prompt>"`. Streams progress to stderr; prints only the final agent message to stdout. `--json` turns stdout into a JSONL event stream (events: `thread.started`, `turn.started`, `turn.completed`, `turn.failed`, `item.*`, `error`).
- WORKER_RESULT survival: the model's final message goes to stdout (text mode) or arrives as agent-message `item.*` events (`--json`). A fenced `WORKER_RESULT` block in the final message survives. Bonus: `--output-schema <schema.json>` can validate the final response against a JSON Schema, and `-o <file>` writes the final message to a file - either could harden envelope capture. **VERIFY** the block is not reformatted in `--json` mode.
- Brief delivery: prompt arg, or piped stdin. If both, the prompt arg is the instruction and piped stdin is added as context. So stdin delivery works like Claude Code.
- Model: `--model` / `-m`, or `--config model=<id>`. Per-run override.
- Auth: reuses saved CLI auth (ChatGPT subscription via `codex login`) by default. API-key path uses `CODEX_API_KEY` (exec-only) or `OPENAI_API_KEY` via login. To force subscription auth, do not set `CODEX_API_KEY` / `OPENAI_API_KEY` on the child env - mirrors the Claude Code `ANTHROPIC_API_KEY`-strip pattern.
- Output-token cap: no documented single env var equivalent to `CLAUDE_CODE_MAX_OUTPUT_TOKENS`; likely via `--config`. **VERIFY** / may not exist.
- Tools/permissions: not a per-tool allowlist. Sandbox policy `-s read-only|workspace-write` / `--sandbox`, approval `-a`, `--full-auto`. `WorkerOptions.AllowedTools` does not map cleanly - drop it or map to a sandbox/approval choice.
- Usage/cost: token usage is in the `--json` events (e.g. `turn.completed`); model is in the events. No USD cost. Vendor string: `openai`.
- Progress digester: `--json` event stream is digestible - a `CodexProgressDigester` is feasible (parse `item.*` / command-execution events). Optional.
- Local-model note: `codex exec --oss` fronts a local Ollama instance - a possible later bridge to the deferred HTTP-agent case, still via this CLI.

## Gemini CLI (Google)

Strong fit. Cleanest structured output of the three.

- Non-interactive (headless): `-p` / `--prompt "<prompt>"`, or any non-TTY run. `--output-format text|json|stream-json` (or `GEMINI_OUTPUT_FORMAT`). `json` returns one object with `.response` (model text) and `.stats` (`.stats.models[<model>].tokens.total`, `.stats.tools`). `stream-json` is a JSONL event stream.
- WORKER_RESULT survival: the model's text lands in `.response` (json) or raw stdout (text). A fenced `WORKER_RESULT` block survives in `.response`. Gemini also supports a response schema for enforced structured output. **VERIFY** the block is intact inside `.response` and not escaped oddly.
- Brief delivery: `-p` arg, or stdin (stdin is prepended to the `--prompt` argument). stdin delivery works.
- Model: `--model` / `-m <id>` (e.g. a `gemini-3-*` id). The sizes map holds these ids.
- Auth: OAuth (Google account / Code Assist free tier) by default; or `GEMINI_API_KEY` / `GOOGLE_API_KEY`; or Vertex (`GOOGLE_APPLICATION_CREDENTIALS` + `GOOGLE_CLOUD_PROJECT` + `GOOGLE_GENAI_USE_VERTEXAI`). To force OAuth/subscription, do not set `GEMINI_API_KEY` / `GOOGLE_API_KEY` - mirrors the strip pattern.
- Output-token cap: no obvious flag equivalent; likely a settings/config value. **VERIFY** / may not exist.
- Tools/permissions: not per-tool. `--yolo` auto-approves all tools; `--approval-mode`. `AllowedTools` does not map cleanly - drop or map to an approval mode.
- Usage/cost: model + token totals in `.stats` (json). No USD cost. Vendor string: `google`.
- Progress digester: `stream-json` is digestible - a `GeminiProgressDigester` is feasible. Optional.

## GitHub Copilot CLI

Medium fit. Works headless, but the weakest on machine-readable output and the finickiest on auth - it is the one most likely to force a contract caveat.

- Non-interactive (programmatic): `copilot -p "<prompt>"` / `--prompt`. `-s` / `--silent` suppresses session metadata to give clean text. `--no-ask-user` stops the agent from pausing to ask clarifying questions (needed for headless - otherwise it can block). No documented `--output-format json` / structured stream like Codex/Gemini.
- WORKER_RESULT survival: with `-s` the model's response is clean text on stdout, so a fenced `WORKER_RESULT` block should survive - but there is no structured wrapper, and without `-s` the output carries session/model metadata noise. **VERIFY** (highest-risk of the three) that the fenced block emerges intact under `-s --no-ask-user`, and confirm whether any usage/model line is still recoverable in that mode.
- Brief delivery: `-p` arg; piped stdin is IGNORED when `-p` is present. So deliver via `-p`, or via stdin without `-p`. Large briefs through `-p` risk arg-length limits - prefer stdin-without-`-p`. **VERIFY** stdin-without-`-p` triggers programmatic (non-interactive) mode.
- Model: `--model <id>`; persisted via `~/.copilot/config.json` or the `/model` slash command. Models are GitHub-hosted (e.g. Claude Sonnet 4.5, GPT family) carrying premium-request multipliers; or BYOK via `COPILOT_PROVIDER_BASE_URL` / `COPILOT_PROVIDER_API_KEY` (OpenAI-compatible/Azure/Anthropic/Ollama). The sizes map holds GitHub model ids (or BYOK ids).
- Auth: GitHub credentials. Headless needs `COPILOT_GITHUB_TOKEN` / `GH_TOKEN` / `GITHUB_TOKEN` set to a user-owned fine-grained PAT with the Copilot Requests permission. Opposite of the strip pattern: you SET a token rather than strip one, and an env token silently overrides stored OAuth. Known rough edges: several open issues report headless `GH_TOKEN` auth failing or the token format being rejected client-side - budget time to get this working. **VERIFY** headless auth end-to-end before committing to Copilot.
- Output-token cap: no documented equivalent. **VERIFY** / likely none.
- Tools/permissions: richest of the three and the closest to `--allowedTools`. `--allow-tool` / `--deny-tool` with filters (e.g. `shell(rm)`, `write`, `url`, MCP server kinds), `--allow-all-tools` / `--allow-all`. `WorkerOptions.AllowedTools` maps here.
- Usage/cost: no per-run token/cost in the CLI's own output is documented - GitHub's agentic-workflows team had to capture usage via an API proxy because the CLI output was insufficient. Cost is premium-request quota (per-model multiplier), not USD. So the Copilot agent's `llm_usage` is likely partial: vendor `github`, model where recoverable, tokens/cost possibly unavailable without a proxy. **VERIFY** what usage, if any, is exposed in `-s` mode.
- Progress digester: no structured stream -> return null. (This is the nullable-digester case the foundation already accommodates.)

## Tool vocabulary mapping

Claude Code tool names appear in the brief templates' instructions (e.g. "use Grep/Glob/Read"). The existing claude-config port (`copilot-prompts/`, `plane-ticket-workflow/`) preserves the Claude vocabulary and adapts at the shell/behavior layer rather than renaming tools. Per-agent template variants (Brief 13's future consumers) should phrase instructions in each agent's own taxonomy where it differs.

| Capability | Claude Code | Codex | Gemini | Copilot |
|---|---|---|---|---|
| Read a file | Read | built-in file read (no fixed public tool name; sandboxed fs) | built-in read; `@file` injects file content | built-in read |
| Search content | Grep | built-in (ripgrep-style) | built-in / shell `grep` | built-in |
| Find by pattern | Glob | built-in glob | built-in glob | built-in |
| Edit / write | Edit / Write | apply-patch / file writes (gated by sandbox) | file write (gated by approval) | write tool (`--allow-tool=write`) |
| Run shell | Bash | shell exec (gated by `--sandbox`/`-a`) | shell (gated by `--yolo`/approval) | `shell` tool (`--allow-tool='shell(...)'`) |
| Web fetch | WebFetch/WebSearch | `web_search` (appears in `--json`) | web tooling (varies) | `url` tool (`--allow-tool=url`) |

The practical takeaway for template variants: Codex and Gemini expose their file/search/edit/shell capabilities as a built-in tool loop rather than as the fixed named tools Claude exposes, so a variant should describe the action ("read the file", "search the repo", "run the test command") rather than name a tool. Copilot is the only one with a Claude-like per-tool permission vocabulary worth naming explicitly (because `--allow-tool` gates them).

## Mapping to ClaudeCodeOptions / ClaudeCodeAgent

| Claude Code surface | Codex | Gemini | Copilot | Clean map? |
|---|---|---|---|---|
| Non-interactive invocation (`--print --verbose --output-format stream-json`) | `codex exec [--json]` | `gemini -p [--output-format json\|stream-json]` | `copilot -p -s --no-ask-user` | yes (Codex/Gemini), partial (Copilot - text only) |
| Brief on stdin | stdin (as context) or prompt arg | stdin (prepended) or `-p` | `-p` arg, or stdin without `-p` | yes, with per-agent argv |
| Terminal result + WORKER_RESULT block | final message (stdout / item events) | `.response` | clean stdout (`-s`) | yes / yes / VERIFY |
| `--model` (from sizes map) | `--model` / `--config model=` | `--model` | `--model` (GitHub or BYOK ids) | yes |
| Strip auth env for subscription (`ANTHROPIC_API_KEY`) | unset `CODEX_API_KEY`/`OPENAI_API_KEY` | unset `GEMINI_API_KEY`/`GOOGLE_API_KEY` | SET `GH_TOKEN`/`COPILOT_GITHUB_TOKEN` (inverse) | yes / yes / different model |
| `CLAUDE_CODE_MAX_OUTPUT_TOKENS` | via `--config`? | settings? | none documented | no clean map - per-agent, may not exist |
| `WorkerOptions.AllowedTools` | sandbox/approval policy | `--yolo`/approval mode | `--allow-tool`/`--deny-tool`/`--allow-all-tools` | only Copilot maps cleanly; drop/repurpose for the others |
| `llm_usage` (model, vendor, tokens, cost) | tokens+model in `--json`; vendor `openai`; no USD | tokens+model in `.stats`; vendor `google`; no USD | partial; vendor `github`; premium-request quota not USD | model/vendor yes; tokens yes (Codex/Gemini) / VERIFY (Copilot); cost_usd null for all three |
| `IWorkerProgressDigester` | feasible (`--json` events) | feasible (`stream-json`) | null (no stream) | optional - nullable already supported |

## Contract implications for the foundation

These do not change op-14 as written, but confirm its shape and flag what each future agent op-doc must handle:

- `WorkerResultParser` reuse holds for Codex and Gemini (the WORKER_RESULT block survives in stdout / `.response`). Copilot is the one to prove first - if the block does not survive cleanly under `-s`, the Copilot op-doc needs a per-agent extraction step, not a contract change.
- `WorkerOptions.AllowedTools` is genuinely Claude/Copilot-shaped. The op-doc already treats it as droppable per agent; Codex/Gemini map it to a sandbox/approval choice or ignore it. No contract change needed.
- Output-token cap is not portable - it is an agent-specific option (or absent). Keep it inside each agent's options/config, not in the shared contract. Consistent with op-14 Brief 02 (per-agent config sub-tables).
- `cost_usd` is null for all three new agents (they emit tokens, not USD); only Claude Code's `total_cost_usd` populates it. Brief 08's nullable `cost_usd` plus per-agent `vendor` plus per-model token counts is the correct shape; `analyze-event-log`'s pricing table needs token-based per-model entries for `openai`/`google`, and a note that `github` (Copilot) bills in premium-request quota, not USD, so it may not price in dollars at all.
- Auth posture splits two ways: strip-to-subscription (Codex, Gemini - same as Claude Code) vs set-a-token (Copilot). Each agent owns its env handling (op-14 Brief 02 OOS already says so), so this is per-agent code, not a contract issue.

## Verify-locally checklist (run against installed binaries)

1. WORKER_RESULT survival per CLI, especially Copilot under `-s --no-ask-user`.
2. Exact terminal-result location and whether `--json` / `--output-format json` reformats a fenced block.
3. Brief delivery: confirm stdin path per CLI (Copilot stdin-without-`-p` triggering programmatic mode).
4. Auth: confirm the subscription/seat path works headless (Codex `codex login`, Gemini OAuth, Copilot PAT) and which env var to set or strip.
5. Model ids accepted by `--model` for the sizes map.
6. Whether any output-token cap exists per CLI.
7. What usage/token data is actually emitted (Copilot is the open question).

## Sources

- Codex CLI: developers.openai.com/codex/noninteractive, /codex/cli/reference, /codex/cli/features
- Gemini CLI: github.com/google-gemini/gemini-cli docs/cli/headless.md; geminicli.com/docs/cli/headless, /docs/reference/configuration
- Copilot CLI: docs.github.com/copilot/reference/copilot-cli-reference/cli-programmatic-reference, /how-tos/copilot-cli/automate-copilot-cli/run-cli-programmatically, /concepts/agents/about-copilot-cli, set-up-copilot-cli/authenticate-copilot-cli; github.com/github/copilot-cli issues #355, #2431, community discussion #167158
- Cross-agent usage normalization: github.blog/ai-and-ml/github-copilot/improving-token-efficiency-in-github-agentic-workflows
- Prior claude-config port: `copilot-prompts/`, `plane-ticket-workflow/` (generated by `bin/sync-*` from CLAUDE.md)