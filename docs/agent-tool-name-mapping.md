# Agent Tool Name Mapping and Contract Reference

This document maps Claude Code's tool vocabulary, invocation model, and surface API to Codex, Gemini, and GitHub Copilot CLIs. It serves as the canonical reference for building Workers.Codex, Workers.Gemini, and Workers.Copilot implementations and as a research baseline for future agent CLI integrations.

**Sources:** op-14a-per-agent-notes.md (March-April 2026 research); live CLI inspection (May 2026).

---

## 1. The Worker Agent Contract

All worker agents must satisfy seven architectural requirements derived from `IWorkerAgent` and `ClaudeCodeAgent`:

### 1a. Non-interactive invocation that runs the full tool loop and exits
The agent must accept a single non-interactive invocation (no REPL, no session persistence without explicit `--resume` or `--continue`) and terminate cleanly after the tool loop completes. Brief delivery, tool execution, and result emission all happen in a single subprocess execution.

**Rationale:** Worker dispatchers invoke agents as child processes in isolated directories; re-entrant sessions and persistent state are out of scope.

### 1b. Brief delivery mechanism
The agent must accept the task brief (full instruction text + context files) via one of: stdin piping, command-line argument, or file reference. The mechanism must not limit the brief size (or document the limit clearly).

**Rationale:** Briefs carry full context (codebase excerpts, previous decisions, requirements); they are often multi-KB and are assembled by the caller, not the agent CLI.

### 1c. Terminal result output with WORKER_RESULT JSON block
Agent output must be scannable for a terminal result: a `WORKER_RESULT` fenced JSON block containing `{ status, summary, files_changed, failure_reason, metadata }`. The block must survive to where the worker result parser can locate it (final stdout, a captured response field, or a structured event stream).

**Rationale:** Worker dispatchers parse a normalized result envelope regardless of which agent ran; brief templates instruct agents to emit this block; `WorkerResultParser` provides the unmarshaling.

### 1d. Model selection knob fed by sizes map
The agent must expose a flag (e.g., `--model`) that accepts a model identifier from the sizes map (e.g., `claude-sonnet-4-6`, `gpt-4-turbo`, `gemini-3-5-pro`). The invocation must pass the selected model through so the LLM usage log includes it.

**Rationale:** Phases assign abstract sizes (Small, Medium, Large) to tasks; workers map sizes to vendor-specific model IDs. The result envelope must carry which model actually ran.

### 1e. Auth posture: subscription or seat, not per-token
The agent must default to a subscription/seat billing model (or equivalent) rather than per-token API-key billing. Where possible, the worker should strip or avoid setting API-key env vars so auth falls through to OAuth, GitHub credentials, or saved login state.

**Rationale:** Worker LLM cost is a throughline/operation concern, not a per-token microcharge. Subscription auth centralizes cost tracking.

### 1f. Token and usage data for the event log
The agent's output must convey: model (name string), vendor (e.g., "anthropic", "openai", "google", "github"), and token counts (input, output, cache where applicable). Cost in USD is optional (may be null); per-vendor token counts suffice for pricing tables.

**Rationale:** Throughline build events are timestamped and tagged with usage; `analyze-event-log` consumes this to report cost and utilization per vendor and per phase.

### 1g. Optional progress digester for live feedback
The agent may expose a structured stream (NDJSON events, JSON array items, or similar) from which a progress digester can extract human-readable status updates (e.g., "tool_use: Bash", "turn: 3", "total tokens: 4200"). A worker that provides no stream must set its digester to null; callers already handle this.

**Rationale:** Long-running operations benefit from live progress; short operations don't need it. Making it optional preserves backward compatibility.

---

## 2. Per-Agent Contract Details

### Codex (OpenAI)

**Invocation (non-interactive):**
```
codex exec "<prompt>" [--json] [--model <model-id>] [--config <key>=<val>]
```
- `--json` emits JSONL event stream (events: `thread.started`, `turn.started`, `turn.completed`, `item.*`, `error`).
- Without `--json`, prints only the final agent message to stdout.
- Progress: stderr streams diagnostic output; `--json` provides structured per-turn events.

**Brief delivery:**
- Prompt argument: `codex exec "<brief_text>"`.
- Piped stdin: brief is prepended to the prompt arg (or becomes the sole input if no `--prompt` arg). stdin delivery works like Claude Code.

**WORKER_RESULT survival:**
- The model's final message (text mode) or final `item.message` event (--json mode) contains the WORKER_RESULT block.
- Confirmed: the fenced block survives intact to stdout or the event stream. TBD: whether `--output-schema` reformats the block or preserves whitespace.

**Model flag and accepted IDs:**
- `--model` / `-m` or `--config model=<id>`.
- Accepts OpenAI model IDs (e.g., `gpt-4-turbo`, `o1-preview`). Sizes map holds these.

**Auth env var:**
- Saved CLI auth via `codex login` (ChatGPT subscription).
- API-key override: `CODEX_API_KEY` (exec-only) or `OPENAI_API_KEY` via login.
- To force subscription auth, do not set `CODEX_API_KEY` or `OPENAI_API_KEY` on the child process env (mirrors Claude Code's `ANTHROPIC_API_KEY` strip pattern).

**Output-token cap:**
- TBD - no documented single env var equivalent to `CLAUDE_CODE_MAX_OUTPUT_TOKENS`. Likely via `--config` or not exposed at the CLI level.

**Tools/permissions:**
- Not per-tool allowlist. Sandbox policy: `-s read-only|workspace-write` / `--sandbox`, approval `-a`, `--full-auto`.
- `WorkerOptions.AllowedTools` does not map cleanly to Codex's permission model. Recommendation: drop it or map to a sandbox/approval choice per agent.

**Usage and cost:**
- Token usage in `--json` events (e.g., `turn.completed` carries `.stats.tokens`).
- Model in event stream. No USD cost field (API billing); vendor string: `openai`.

**Progress digester:**
- `--json` event stream is digestible. A `CodexProgressDigester` parsing `item.*` / `turn.completed` events is feasible and optional.

**Local-model note:**
- `codex exec --oss` fronts a local Ollama instance. Possible future bridge to deferred HTTP-agent case.

---

### Gemini (Google)

**Invocation (non-interactive / headless):**
```
gemini -p "<prompt>" [--model <model-id>] [--output-format text|json|stream-json]
```
- `-p` / `--prompt "<prompt>"` triggers non-interactive (headless) mode.
- Non-TTY input also triggers headless. Any non-interactive run works.
- `--output-format`: `text` (plain text), `json` (single object with `.response` and `.stats`), `stream-json` (JSONL events).

**Brief delivery:**
- Prompt argument: `-p "<brief_text>"`.
- Piped stdin: brief is prepended to the `--prompt` argument (or becomes input if no `-p`). stdin delivery works like Claude Code.

**WORKER_RESULT survival:**
- `json`: model text lands in `.response` field. WORKER_RESULT block survives intact inside `.response`.
- `text`: raw stdout. WORKER_RESULT block survives to stdout.
- Confirmed: block survives in `.response`. TBD: escaping behavior inside `.response` (e.g., newlines, quotes).

**Model flag and accepted IDs:**
- `--model` / `-m <id>` (e.g., `gemini-3-5-pro`, `gemini-2-flash-exp`).
- Sizes map holds these IDs.

**Auth env var:**
- OAuth (Google account / Code Assist free tier) by default.
- API-key override: `GEMINI_API_KEY` / `GOOGLE_API_KEY`.
- Vertex AI: `GOOGLE_APPLICATION_CREDENTIALS` + `GOOGLE_CLOUD_PROJECT` + `GOOGLE_GENAI_USE_VERTEXAI`.
- To force OAuth/subscription, do not set `GEMINI_API_KEY` / `GOOGLE_API_KEY` on the child process env (mirrors Claude Code pattern).

**Output-token cap:**
- TBD - no obvious flag equivalent. Likely a settings/config value or not exposed at the CLI level.

**Tools/permissions:**
- Not per-tool allowlist. Approval mode: `--yolo` (auto-approve all), `--approval-mode default|auto_edit|yolo|plan`.
- `WorkerOptions.AllowedTools` does not map cleanly. Recommendation: drop it or map to approval mode per agent.

**Usage and cost:**
- Token counts and model info in `.stats` (json mode only). `.stats.models[<model>].tokens.total`, `.stats.tools`.
- No USD cost. Vendor string: `google`.

**Progress digester:**
- `stream-json` is NDJSON events. A `GeminiProgressDigester` parsing events is feasible and optional.

---

### GitHub Copilot

**Invocation (non-interactive):**
```
copilot -p "<prompt>" [-s|--silent] [--no-ask-user] [--model <model-id>] [--allow-all-tools]
```
- `-p` / `--prompt "<prompt>"` triggers non-interactive scripting mode (headless).
- `-s` / `--silent` suppresses session metadata to give clean text output (required for envelope capture).
- `--no-ask-user` stops the agent from pausing to ask clarifying questions (required for headless, otherwise it blocks).
- No documented `--output-format json` equivalent like Codex/Gemini. This is the weakest structured output of the three.

**Brief delivery:**
- Prompt argument: `-p "<brief_text>"`.
- Piped stdin without `-p`: brief on stdin triggers headless mode. If `-p` is present, stdin is IGNORED.
- TBD: confirm stdin-without-`-p` triggers programmatic mode consistently.

**WORKER_RESULT survival:**
- With `-s --no-ask-user`, model response is clean text on stdout, so a fenced WORKER_RESULT block should survive.
- Without `-s`, output carries session/model metadata noise.
- HIGHEST RISK of the three: TBD - confirm fenced block emerges intact under `-s --no-ask-user`, and confirm whether any usage/model line is still recoverable in that stripped mode.

**Model flag and accepted IDs:**
- `--model <id>`.
- GitHub-hosted models (e.g., `Claude Sonnet 4.5`, `GPT-4o`) carrying premium-request multipliers, or BYOK (Bring Your Own Key) via `COPILOT_PROVIDER_BASE_URL` / `COPILOT_PROVIDER_API_KEY` (OpenAI-compatible, Azure, Anthropic, Ollama).
- Sizes map holds GitHub model IDs or BYOK IDs.

**Auth env var:**
- GitHub credentials required for headless. `COPILOT_GITHUB_TOKEN` / `GH_TOKEN` / `GITHUB_TOKEN` must be set to a user-owned fine-grained PAT with the Copilot Requests permission.
- Opposite of the strip pattern: you SET a token rather than strip one.
- Known rough edges: several open GitHub issues report headless auth failing or token format rejection client-side. Plan time to debug this in the op-doc.
- TBD: confirm headless auth end-to-end before committing to Copilot.

**Output-token cap:**
- TBD - no documented equivalent. Likely none.

**Tools/permissions:**
- Richest of the three and closest to `--allowedTools`. `--allow-tool` / `--deny-tool` with filters (e.g., `shell(rm)`, `write`, `url`, MCP server kinds), `--allow-all-tools` / `--allow-all`.
- `WorkerOptions.AllowedTools` maps here most cleanly (only Copilot).

**Usage and cost:**
- TBD - no per-run token/cost documented in CLI output. GitHub's agentic-workflows team had to proxy CLI output to capture usage because the CLI itself was insufficient.
- Cost billed in premium-request quota (per-model multiplier), not USD.
- Worker's `llm_usage`: vendor `github`, model where recoverable, tokens and cost possibly unavailable without a proxy.
- TBD: what usage, if any, is exposed in `-s` mode.

**Progress digester:**
- No structured event stream documented. Return null (the foundation already supports nullable digesters).

**Headless auth and output concerns:**
- Copilot is the one most likely to require contract caveats or per-agent workarounds.

---

## 3. Tool Vocabulary Mapping

Claude Code tool names appear in brief templates as instructions (e.g., "use Grep/Glob/Read"). The existing claude-config port (copilot-prompts/, plane-ticket-workflow/) preserves Claude vocabulary and adapts at the shell/behavior layer. Per-agent template variants should phrase instructions in each agent's native taxonomy where it differs.

| Capability | Claude Code | Codex | Gemini | Copilot |
|---|---|---|---|---|
| Read a file | Read | built-in file read (no fixed public tool name; sandboxed fs) | built-in read; `@file` injects content | built-in read |
| Search content | Grep | built-in (ripgrep-style) | built-in / shell `grep` | built-in |
| Find by pattern | Glob | built-in glob | built-in glob | built-in |
| Edit / write | Edit / Write | apply-patch / file writes (gated by sandbox) | file write (gated by approval) | write tool (`--allow-tool=write`) |
| Run shell | Bash | shell exec (gated by `--sandbox` / `-a`) | shell (gated by `--yolo` / approval) | `shell` tool (`--allow-tool='shell(...)'`) |
| Web fetch | WebFetch / WebSearch | `web_search` (appears in `--json` events) | web tooling (varies by extension) | `url` tool (`--allow-tool=url`) |

**Practical takeaway:** Codex and Gemini expose file/search/edit/shell capabilities as a built-in tool loop rather than as fixed named tools. Template variants should describe the action ("read the file", "search the repo") rather than name a tool. Copilot is the only one with a Claude-like per-tool permission vocabulary worth naming explicitly in templates (because `--allow-tool` gates them).

---

## 4. ClaudeCodeOptions / ClaudeCodeAgent Surface Mapping

| Surface (Claude Code) | Codex | Gemini | Copilot | Clean map? | Notes |
|---|---|---|---|---|---|
| **Non-interactive invocation** (`--print --verbose --output-format stream-json`) | `codex exec [--json]` | `gemini -p [--output-format json\|stream-json]` | `copilot -p -s --no-ask-user` | yes (Codex/Gemini), partial (Copilot text only) | Copilot has no structured event stream; text only. |
| **Brief on stdin** | stdin or prompt arg | stdin or `-p` | `-p` arg, or stdin without `-p` | yes | All three support stdin; per-agent argv differs. |
| **Terminal result + WORKER_RESULT block** | final message (stdout / item events) | `.response` (json) / stdout (text) | clean stdout (`-s`) | yes / yes / VERIFY | Copilot's `-s` output is plaintext; block survival TBD. |
| **`--model` (from sizes map)** | `--model` / `--config model=` | `--model` | `--model` (GitHub or BYOK ids) | yes | All three map cleanly. Ids are vendor-specific. |
| **Strip auth env (subscription)** | unset `CODEX_API_KEY` / `OPENAI_API_KEY` | unset `GEMINI_API_KEY` / `GOOGLE_API_KEY` | SET `GH_TOKEN` / `COPILOT_GITHUB_TOKEN` (inverse pattern) | yes / yes / different | Copilot requires setting a token, not stripping one. |
| **`CLAUDE_CODE_MAX_OUTPUT_TOKENS`** | via `--config`? | settings? | none documented | no clean map | Per-agent; may not exist (TBD). |
| **`WorkerOptions.AllowedTools`** | sandbox/approval policy | `--yolo` / approval mode | `--allow-tool` / `--deny-tool` / `--allow-all-tools` | only Copilot maps cleanly | Drop/repurpose for Codex and Gemini. |
| **`llm_usage` (model, vendor, tokens, cost)** | tokens + model in `--json`; vendor `openai`; no USD | tokens + model in `.stats`; vendor `google`; no USD | partial; vendor `github`; premium quota not USD | model/vendor yes; tokens yes (Codex/Gemini) / VERIFY (Copilot); cost_usd null for all three | Only Claude Code exposes `cost_usd`. All emit tokens. |
| **`IWorkerProgressDigester`** | feasible (`--json` events) | feasible (`stream-json` events) | null (no stream) | optional (nullable supported) | Copilot has no structured event stream. |

---

## 5. Contract Implications for the Foundation

These findings confirm op-14's architecture and flag what each future agent op-doc must handle:

### WorkerResultParser reuse
The WORKER_RESULT block survives cleanly in Codex and Gemini (stdout / `.response`). Copilot is the highest risk - if the block does not survive cleanly under `-s`, the Copilot op-doc needs a per-agent extraction step, not a contract change.

### AllowedTools mapping
`WorkerOptions.AllowedTools` is Claude/Copilot-shaped. The op-14 architecture already treats it as droppable per agent; Codex/Gemini map it to sandbox/approval choices or ignore it. No contract change needed.

### Output-token cap portability
Not portable - it is an agent-specific option (or may not exist). Keep it inside each agent's options/config, not in the shared contract. Consistent with op-14 Brief 02 (per-agent config sub-tables).

### Cost billing models
`cost_usd` is null for all three new agents (they emit tokens, not USD); only Claude Code's `total_cost_usd` populates it. Brief 08's nullable `cost_usd` plus per-agent `vendor` plus per-model token counts is the correct shape. `analyze-event-log`'s pricing table needs token-based per-model entries for `openai` / `google`, and a note that `github` (Copilot) bills in premium-request quota, not USD, so it may not price in dollars at all.

### Auth posture split
Auth splits two ways: strip-to-subscription (Codex, Gemini - same as Claude Code) vs set-a-token (Copilot). Each agent owns its env handling (op-14 Brief 02 already says so), so this is per-agent code, not a contract issue.

---

## 6. Verify-Locally Checklist

These items MUST be confirmed by live binary testing before the agent op-doc commits to implementation:

- **VERIFY:** WORKER_RESULT fenced block survival per CLI, especially Copilot under `-s --no-ask-user`.
- **VERIFY:** Exact terminal-result location and whether `--json` / `--output-format json` reformats or escapes a fenced block.
- **VERIFY:** Brief delivery - confirm stdin path per CLI (Copilot stdin-without-`-p` triggering programmatic mode).
- **VERIFY:** Auth - confirm subscription/seat path works headless (Codex `codex login`, Gemini OAuth, Copilot PAT) and which env var to set or strip.
- **VERIFY:** Model ids accepted by `--model` for the sizes map (live listing or docs).
- **VERIFY:** Whether any output-token cap exists per CLI.
- **VERIFY:** What usage/token data is actually emitted (Copilot is the open question).
- **VERIFY:** Copilot headless auth end-to-end before committing.

---

## 7. Sources

- **op-14a-per-agent-notes.md** - Primary research document (ThroughlineBuild project), March-April 2026.
- **Codex CLI:** developers.openai.com/codex/noninteractive, /codex/cli/reference, /codex/cli/features.
- **Gemini CLI:** github.com/google-gemini/gemini-cli docs/cli/headless.md; geminicli.com/docs/cli/headless, /docs/reference/configuration. Live binary: `gemini --help` (May 2026).
- **Copilot CLI:** docs.github.com/copilot/reference/copilot-cli-reference/cli-programmatic-reference, /how-tos/copilot-cli/automate-copilot-cli/run-cli-programmatically, /concepts/agents/about-copilot-cli, /set-up-copilot-cli/authenticate-copilot-cli; github.com/github/copilot-cli issues #355, #2431, community discussion #167158. Live binary: `copilot --help` (May 2026).
- **Claude Code:** IWorkerAgent.cs, ClaudeCodeAgent.cs, ClaudeCodeOptions.cs, WorkerResultParser.cs (ThroughlineBuild.Contracts, ThroughlineBuild.Workers.ClaudeCode). Live binary: `claude --help` (May 2026).
- **Cross-agent usage normalization:** github.blog/ai-and-ml/github-copilot/improving-token-efficiency-in-github-agentic-workflows.
- **Prior port:** copilot-prompts/, plane-ticket-workflow/ (generated by bin/sync-* from CLAUDE.md).

---

## Document metadata

- **Created:** 2026-05-28
- **Ticket:** TLB-201 (D.14: tool-name-research-doc)
- **Status:** Complete (research phase; op-docs for individual agents are out of scope)
- **Contact:** dan@freepdx.com
