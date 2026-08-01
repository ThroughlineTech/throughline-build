# Agent Instruction Rule Inventory

This inventory records the normative rules in the scoped repository `AGENTS.md`
tree and the `run-backlog` skill before behavior-preserving compaction.

Columns:

- `Source`: baseline file and line or line range.
- `Summary`: current rule wording in compact form.
- `Destination`: canonical home after compaction.
- `Duplicate handling`: whether repeat text may become a cross-reference.

## Shell And Platform

| ID | Source | Summary | Destination | Duplicate handling |
| --- | --- | --- | --- | --- |
| SH-001 | `run-backlog/SKILL.md:12-13` | Run every documented CLI command with Bash; on Windows use Git Bash; do not translate to PowerShell, cmd.exe, or another shell; quote path variables. | `run-backlog/SKILL.md` | Keep once in skill; references inherit it. |
| SH-002 | `run-backlog/SKILL.md:15-20` | Resolve repository root from nested directories with `git rev-parse --show-toplevel` and `cd "$REPO_ROOT"`. | `run-backlog/SKILL.md` | Keep in skill; references may link. |
| SH-003 | `run-backlog/references/parallel-fan-out.md:15` | Fan-out CLI commands run in Bash with quoted path variables. | `run-backlog/SKILL.md` | Replace with inheritance note. |
| SH-004 | `run-backlog/references/repo-adaptation.md:12-24` | Adaptation commands run in Bash/Git Bash, not PowerShell; start from repo root; inspect repo instructions and preserve unrelated changes before editing. | `run-backlog/SKILL.md` plus `repo-adaptation.md` | Collapse shell repeat, keep adaptation-specific inspection rule. |
| SH-005 | `AGENTS.md:44-45` | Build/test/publish require .NET 10 SDK, `git` on `PATH`, and native toolchain for AOT publish. | `AGENTS.md` | No duplicate needed. |
| SH-006 | `AGENTS.md:61-64` | Fresh clones have no `bin/` or `.build/`; build the binary before use; root `Directory.Build.*` files are machine-local and optional for tests. | `AGENTS.md` | No duplicate needed. |

## Ticket And Plane Access

| ID | Source | Summary | Destination | Duplicate handling |
| --- | --- | --- | --- | --- |
| TK-001 | `AGENTS.md:100-104` | The repo tracks work in Plane via the `build` CLI; `.build/config.toml` is gitignored; run `build init`/`build setup` for backend configuration or use GitHub issues/PRs instead. | `AGENTS.md` | Keep root authoritative. |
| TK-002 | `AGENTS.md:106-108` | When backend is configured, all ticket operations go through `build`; no direct REST, MCP ticket server, or `/ticket-*` slash flow. | `AGENTS.md` | Keep root authoritative. |
| TK-003 | `AGENTS.md:108-112` | `--json` emits only the versioned envelope on stdout; diagnostics go to stderr; exit codes are 0 ok, 1 failure, 2 usage/config, 3 missing secret. | `AGENTS.md` | Keep root, shorten around command table if possible. |
| TK-004 | `AGENTS.md:114-124` | Safe ticket commands are `build list/get/comments/comment/transition/relate/close/defer/reopen/amend/new`; do not guess verbs or formats. | `AGENTS.md` | Preserve command list; details can defer to `build --help`. |
| TK-005 | `AGENTS.md:126` | Bare ticket numbers expand to the configured project ID. | `AGENTS.md` | Keep root. |
| TK-006 | `AGENTS.md:130-134` | Investigate tickets by reading ticket and comments, then comment findings; work tickets by reading, implementing, gating, then commenting/transitioning. | `AGENTS.md` | Keep root. |
| TK-007 | `AGENTS.md:136-138` | Use `build new --print-template` for new ticket bodies so accepted headings are present. | `AGENTS.md` | Keep root. |
| TK-008 | `AGENTS.md:142-148` | Because this repo edits `build`, verify read-only paths before live mutations when changing ticket client/transport/retry/state/serialization; inspect JSON envelopes and defer final ticket updates until a fresh binary is known good. | `AGENTS.md`; echoed in `run-backlog/SKILL.md` | Keep root plus short skill caution. |
| TK-009 | `run-backlog/SKILL.md:22-23` | Use `build --help` and `build <command> --help` for command contracts; treat `build help <topic>` as prose only. | `run-backlog/SKILL.md` | Keep skill. |
| TK-010 | `run-backlog/SKILL.md:35` | Do not run or mutate real backlog tickets during installation validation. | `run-backlog/SKILL.md` and `repo-adaptation.md` | Keep in skill, detailed fake-ID validation in reference. |
| TK-011 | `run-backlog/SKILL.md:44-46` | Read ticket bodies and comments; verify every acceptance criterion; if a ticket changes its ticket client or transport, verify reads before mutation and inspect mutation responses. | `run-backlog/SKILL.md` | Keep as hard invariant. |
| TK-012 | `run-backlog/SKILL.md:92-93` | Ticket transitions are separate mutations whose responses are inspected and read back; never cascading-close when only one ticket should change. | Serial procedure reference | Move to `serial-loop.md` if created. |
| TK-013 | `run-backlog/references/repo-adaptation.md:30-32` | Discover supported ticket commands and ticket-client mutation safety from the target repo. | `repo-adaptation.md` | Keep adaptation-specific. |
| TK-014 | `run-backlog/references/repo-adaptation.md:39-40` | Do not infer permission to mutate real tickets; if seed/resource decision is ambiguous, remain serial or ask. | `repo-adaptation.md` | Keep, cross-ref skill invariant. |
| TK-015 | `run-backlog/references/repo-adaptation.md:101-103` | Validate adaptation only with fake IDs; inspect registered worktrees/root; never delete an unverified orphan. | `repo-adaptation.md` | Keep detailed validation rule. |

## Branch, Commit, Push, Merge, Deploy

| ID | Source | Summary | Destination | Duplicate handling |
| --- | --- | --- | --- | --- |
| BC-001 | `AGENTS.md:69-70` | Feature work uses `ticket/<id>` or a short descriptive branch; never commit directly to `main`. | `AGENTS.md` | Keep root. |
| BC-002 | `AGENTS.md:71-72` | Commit messages use `{TICKET-ID}: short description` for tickets or `topic: short description`; no vendor branding, sign-offs, or generated-with lines. | `AGENTS.md` | Keep root. |
| BC-003 | `AGENTS.md:73-74` | Implementing and shipping are separate; do not merge, push to `main`, tag, or release without explicit instruction. | `AGENTS.md` | Keep root. |
| BC-004 | `run-backlog/SKILL.md:39-42` | The conductor alone owns commits, branches, integration, ticket mutations, and worktree lifecycle; implementers/reviewers never commit, push, switch/create/delete branches, mutate tickets, deploy, integrate, or tear down worktrees. | `run-backlog/SKILL.md` plus `agent-contracts.md` | Keep invariant in skill and worker contract detail in reference. |
| BC-005 | `run-backlog/SKILL.md:47` | Never deploy, push, merge to primary, or ship without explicit user authorization. | `run-backlog/SKILL.md` | Keep hard invariant. |
| BC-006 | `run-backlog/SKILL.md:51-65` | Do not stop merely because primary worktree is on primary branch; safely create/switch run branch; derive branch name; use configured base; refuse only dirty tracked changes, branch ambiguity, or unrelated unsafe state; set `INTEGRATION_TARGET` internally when absent; do not ask the human to do safe branch setup manually. | `run-backlog/SKILL.md` | Keep branch bootstrap in skill. |
| BC-007 | `run-backlog/SKILL.md:89` | Commit only after review passes and only when repo workflow calls for a commit. | Serial procedure reference | Move to `serial-loop.md` if created. |
| BC-008 | `run-backlog/references/agent-contracts.md:51-60` | Conductor integration sequence: commit reviewed diff, rebase helper branch, fast-forward shared branch, stop on conflicts, run integrated gate, mutate ticket state and tear down only after gate passes. | `agent-contracts.md` and `parallel-fan-out.md` | Keep detailed integration contracts where needed. |
| BC-009 | `run-backlog/references/agent-contracts.md:62-63` | Optional integration reviewer may inspect and gate but does not own Git history or ticket state. | `agent-contracts.md` | Keep. |
| BC-010 | `run-backlog/references/parallel-fan-out.md:78-88` | Fan-out integration is serial and deterministic; conductor commits, rebases, fast-forwards, stops on conflicts, gates integrated tree, and mutates tickets one operation at a time only after integrated gate passes. | `parallel-fan-out.md` | Keep fan-out-specific procedure. |
| BC-011 | `run-backlog/references/parallel-fan-out.md:98` | Fan-out never deploys, pushes, merges to primary, or ships implicitly. | `run-backlog/SKILL.md`; short echo in `parallel-fan-out.md` | Keep short safety duplicate. |
| BC-012 | `run-backlog/references/repo-adaptation.md:37-40` | Discover irreversible operations requiring user authorization; do not infer permission to create production resources, deploy, or mutate real tickets. | `repo-adaptation.md` | Keep adaptation-specific. |
| BC-013 | `run-backlog/references/repo-adaptation.md:117-119` | Integration validation is dry-run reasoning; do not merge real work during validation. | `repo-adaptation.md` | Keep. |

## Worktree And Parallelism

| ID | Source | Summary | Destination | Duplicate handling |
| --- | --- | --- | --- | --- |
| WT-001 | `AGENTS.md:80-81` | Parallel work requires separate worktrees; never let two agents edit the same working tree. | `AGENTS.md` | Keep root. |
| WT-002 | `run-backlog/SKILL.md:27-35` | Serial mode is default and required when overlap/dependencies/safety/worker availability is uncertain; fan-out only by user request or repo declaration with Build-backed config; read fan-out reference first; stale declarations stay serial; read adaptation reference for installation. | `run-backlog/SKILL.md` | Keep mode router. |
| WT-003 | `run-backlog/SKILL.md:70-73` | Serial inventory discovers authorized scope, expands shorthand, orders prerequisites first, uses numeric order only among equally ready tickets, and rejects cycles/unverified dependencies outside scope. | Serial procedure reference | Move to `serial-loop.md` if created. |
| WT-004 | `run-backlog/SKILL.md:95-97` | Start next ticket only after current ticket is complete or blocked; persistence requests apply only to authorized queue and do not authorize unrelated mutations. | Serial procedure reference | Move to `serial-loop.md` if created. |
| WT-005 | `run-backlog/references/parallel-fan-out.md:17-29` | Fan-out is opt-in and requires Build-backed ticket commands, source roots, review checks, worktree root/seed allowlist, cap, and serialization rules; stale/incomplete config stays serial; never copy ignored dirs or secrets wholesale. | `parallel-fan-out.md` | Keep exact preconditions. |
| WT-006 | `run-backlog/references/parallel-fan-out.md:32-45` | Wave planning reads every ticket/dependency, predicts files conservatively, marks uncertainty, verifies external deps, runs `build waves`, shows output, stops on nonzero, and serializes uncertain/global/shared changes. | `parallel-fan-out.md` | Keep. |
| WT-007 | `run-backlog/references/parallel-fan-out.md:49-63` | Conductor owns leasing lifecycle; create/select shared branch; lease with `build worktree lease`; use seed only when allowlisted and needed; inspect JSON; Build owns manifest/containment/collision/rollback/helper-branch safety. | `parallel-fan-out.md` | Keep. |
| WT-008 | `run-backlog/references/parallel-fan-out.md:67-76` | Spawn one implementer per leased worktree up to cap; one ticket per worktree; inspect state; fresh read-only reviewer; preserve worktree for rework; no concurrent writers; gate before integration. | `parallel-fan-out.md` | Keep; cross-ref `agent-contracts.md`. |
| WT-009 | `run-backlog/references/parallel-fan-out.md:90-97` | Tear down only safely integrated or authorized abandoned worktrees; preserve failed/conflicted worktrees unless safe recovery is proven; use `build worktree teardown`; use `--force` only with explicit human approval. | `parallel-fan-out.md` | Keep. |
| WT-010 | `run-backlog/references/parallel-fan-out.md:100-133` | `build waves` input schema accepts bare ticket array or object with `cap`, `verifiedExternalDeps`, and `tickets`; conflict and dependency rules determine waves; `--json` for machine output; nonzero exit unsafe. | `parallel-fan-out.md` | Keep exact command contract. |
| WT-011 | `run-backlog/references/repo-adaptation.md:33-36` | Adaptation must discover configured gates, source roots, conflict classes, ignored seed allowlist, unique leases, safe worktree root, and cap 2 or 3. | `repo-adaptation.md` | Keep. |
| WT-012 | `run-backlog/references/repo-adaptation.md:44-61` | Prefer Build fan-out primitives; configure `[worktree]`, `[waves]`, `[[waves.serialize]]`, and `[[review.checks]]`; use current `build init --print-template` and `build <command> --help`, not `build help config` as schema. | `repo-adaptation.md` | Keep. |
| WT-013 | `run-backlog/references/repo-adaptation.md:79-83` | No fan-out declaration means serial; do not implement repo-local worktree/planner helpers unless Build lacks a required primitive and user asks for a temporary bridge. | `repo-adaptation.md` | Keep. |
| WT-014 | `run-backlog/references/repo-adaptation.md:112-123` | Validate worktree fixtures, gate checks, dry-run integration, adapter hooks, and one successful lease/setup/gate/teardown after final config/docs changes. | `repo-adaptation.md` | Keep exhaustive validation. |
| WT-015 | `run-backlog/references/repo-adaptation.md:127-133` | Uncommitted adaptation is absent from worktrees created from `HEAD`; invoke adapted helpers/hooks by absolute primary-tree path during validation; after rework use new reviewer and prove no fake branches/worktrees/manifests/listeners/helpers remain. | `repo-adaptation.md` | Keep. |

## Conductor, Implementer, Reviewer Boundaries

| ID | Source | Summary | Destination | Duplicate handling |
| --- | --- | --- | --- | --- |
| CR-001 | `run-backlog/SKILL.md:8` | Act as conductor; repo instructions and declared ticket client are authoritative. | `run-backlog/SKILL.md` | Keep. |
| CR-002 | `run-backlog/SKILL.md:39-48` | Conductor-only ownership, worker mutation bans, unrelated-change preservation, ticket read/comment/AC verification, ticket-client caution, and explicit authorization for deploy/push/merge/ship. | `run-backlog/SKILL.md` | Keep concise invariant block. |
| CR-003 | `run-backlog/SKILL.md:74-79` | For one ticket, read ticket/comments, classify implementation/investigation/hygiene, inspect for hygiene without manufacturing changes, enter active state only when implementation begins, make smallest complete change, and run risk-proportionate gates. | Serial procedure reference | Move to `serial-loop.md` if created. |
| CR-004 | `run-backlog/SKILL.md:80-87` | Use implementer then fresh read-only reviewer when independent workers exist; give reviewer ticket and diff, not implementer conclusions; reviewer reruns gates; rework returns to same implementer/worktree with new reviewer each round; stop after three failed rounds; disclose if independence unavailable. | Serial procedure reference and `agent-contracts.md` | Move procedural loop, keep worker details in contracts. |
| CR-005 | `run-backlog/SKILL.md:88-93` | Finalize with commit only after passing review and repo workflow calls for one; attach concise evidence; transition tickets separately and read back; no cascading close for one ticket. | Serial procedure reference | Move to `serial-loop.md` if created. |
| CR-006 | `run-backlog/references/agent-contracts.md:3-4` | Conductor remains responsible for detecting accidental state changes after every worker returns. | `agent-contracts.md` | Keep. |
| CR-007 | `run-backlog/references/agent-contracts.md:8-22` | Implementer receives exactly one ticket, full body/AC, workspace path, repo instructions, exact gate; works only in path, preserves changes, runs gate/result, reports files and AC self-check, avoids all mutations, returns structured conductor report. | `agent-contracts.md` | Keep exact worker contract. |
| CR-008 | `run-backlog/references/agent-contracts.md:24` | Never allow two agents to write the same working tree concurrently. | `AGENTS.md` and `agent-contracts.md` | Intentional safety duplicate. |
| CR-009 | `run-backlog/references/agent-contracts.md:28-44` | Reviewer is fresh, read-only, given ticket/AC/path/diff/review invariants/gate, not intended verdict or implementer self-assessment; makes no edits/mutations; inspects diff, reruns gate, checks criteria/risk/scope/invariants, returns exact verdict format; conductor inspects for accidental edits. | `agent-contracts.md` | Keep exact worker contract. |
| CR-010 | `run-backlog/references/agent-contracts.md:46-49` | Rework sends original ticket and findings to same implementer/worktree, then uses a new reviewer; stop after three failed review rounds and report impasse. | `agent-contracts.md` | Keep. |
| CR-011 | `run-backlog/references/agent-contracts.md:65-82` | Codex, Claude Code, and other-agent adapters rely on universal contracts; Claude hooks are Claude-only defense-in-depth and must not be claimed to enforce other agents; shell hooks must parse chained commands, quotes, absolute executables, and Git global options. | `agent-contracts.md` | Keep detailed adapter warning here. |
| CR-012 | `run-backlog/references/repo-adaptation.md:85-97` | Repo instructions must state universal conductor/worker boundaries; add only relevant adapter; do not install/describe Claude hooks as Codex enforcement; do not copy another repo's prefixes, gates, ports, secrets, paths, or agent definitions. | `repo-adaptation.md` | Keep adaptation-specific; cross-ref contracts. |
| CR-013 | `run-backlog/SKILL.md:115-124` | Workflow is universal; Codex boundaries are task contracts, `agents/openai.yaml` is UI metadata, Claude hook is Claude-only defense in depth, other agents need their own tested enforcement. | `agent-contracts.md` with short SKILL router note | Move detail to contracts; keep short pointer. |

## Gates And Validation

| ID | Source | Summary | Destination | Duplicate handling |
| --- | --- | --- | --- | --- |
| GV-001 | `AGENTS.md:55-59` | For any code change, `dotnet build` and full `dotnet test` must be green with output pasted verbatim; partial/filtered tests cannot be reported as done; CI restores/tests/publishes on macOS, Windows, and Linux RIDs. | `AGENTS.md` | Keep root. |
| GV-002 | `AGENTS.md:94-96` | Gates must be able to fail; zero gating checks silently pass; when touching gate/scaffold/verification code, confirm the failure path still fails. | `AGENTS.md` | Keep root. |
| GV-003 | `tests/AGENTS.md:3-6` | One test project mirrors each source project; run all tests with `dotnet test`; test props keep output quiet on green and full on failure. | `tests/AGENTS.md` | Keep local test orientation. |
| GV-004 | `run-backlog/SKILL.md:77-83` | Hygiene work uses focused gates without manufactured changes; implementation uses risk-proportionate gates; reviewer reruns relevant gates. | Serial procedure reference | Move to `serial-loop.md` if created. |
| GV-005 | `run-backlog/SKILL.md:90-91` | Final evidence includes exact gates/counts and whether code changed. | Serial procedure reference | Move to `serial-loop.md` if created. |
| GV-006 | `run-backlog/references/agent-contracts.md:9-17` | Prefer `build gate --ticket <ID> --require-checks --json` when `[[review.checks]]` exists; implementer must run exact gate and report result. | `agent-contracts.md` | Keep. |
| GV-007 | `run-backlog/references/agent-contracts.md:38-41` | Reviewer reruns exact gate and returns verdict with observed gate results and actionable findings. | `agent-contracts.md` | Keep. |
| GV-008 | `run-backlog/references/parallel-fan-out.md:74-76` | In leased worktrees, run `build gate --ticket <ID> --require-checks --json` for implementer and reviewer; do not integrate before PASS plus conductor diff/gate verification. | `parallel-fan-out.md` | Keep. |
| GV-009 | `run-backlog/references/repo-adaptation.md:59-61` | Configure `[[review.checks]]` so `build gate --ticket <ID> --require-checks --json` is real; use current command help for syntax. | `repo-adaptation.md` | Keep. |
| GV-010 | `run-backlog/references/repo-adaptation.md:104-125` | Adaptation validation must cover `build waves` fixture matrix, `build worktree` fixtures, configured gate checks, integration dry-run, adapter behavior, one successful lease/setup/gate/teardown, and repo lint/typecheck/tests/build/smoke as available. | `repo-adaptation.md` | Keep exhaustive validation. |
| GV-011 | `src/ThroughlineBuild.Verification/AGENTS.md:3-5` | Verification is stack-agnostic; never teach it a language or tool; stack knowledge belongs in config/derived data. | `src/ThroughlineBuild.Verification/AGENTS.md` | Keep local hard rule. |
| GV-012 | `src/ThroughlineBuild.Verification/AGENTS.md:7-16` | Gating roles hard-fail; vacuity proof must prove a green gate can fail and hard-fails without rework if vacuous; control prober classifies base failures as environmental and skips siblings without burning rework. | `src/ThroughlineBuild.Verification/AGENTS.md` | Keep local behavior. |
| GV-013 | `src/ThroughlineBuild.Verification/AGENTS.md:17-22` | Obsolete ratifier verifies cited commit/files and model satisfaction; verifier tool enforcement warns when allowed-tools cannot be enforced. | `src/ThroughlineBuild.Verification/AGENTS.md` | Keep. |

## AOT And Serialization

| ID | Source | Summary | Destination | Duplicate handling |
| --- | --- | --- | --- | --- |
| AOT-001 | `AGENTS.md:85-89` | `ThroughlineBuild.Cli` publishes with `PublishAot=true`; use source-generated `JsonSerializerContext`; never rely on reflection serialization; keep Contracts I/O-free; AOT-sensitive tests flip reflection off. | `AGENTS.md` | Keep root. |
| AOT-002 | `src/AGENTS.md:18-20` | `Cli` sets `PublishAot=true`; source-generated JSON only; no reflection serialization; Contracts I/O-free. | `AGENTS.md` and `src/AGENTS.md` | May shorten nested to root cross-reference plus local orientation. |
| AOT-003 | `tests/AGENTS.md:8-12` | Test projects do not inherit `PublishAot`; AOT-sensitive paths must call the reflection-disable switch before parser/serialization tests. | `tests/AGENTS.md` | Keep local detail. |
| AOT-004 | `src/ThroughlineBuild.ClaudeCode/AGENTS.md:8-11` | Facade must delegate transport/transcript logic, preserve AOT-safe serialization, preserve string overload idempotent contract append, and keep advanced `Brief` overload usable without modifying caller instructions. | `src/ThroughlineBuild.ClaudeCode/AGENTS.md` | Keep local nuance. |
| AOT-005 | `src/ThroughlineBuild.Plane/AGENTS.md:24` | Plane JSON uses source-generated context and no reflection serialization. | `src/ThroughlineBuild.Plane/AGENTS.md` | Keep local reminder or cross-ref root with local client context. |
| AOT-006 | `src/ThroughlineBuild.Scaffold/AGENTS.md:23-24` | Scaffold tests include AOT reflection switch-off patterns; keep them. | `src/ThroughlineBuild.Scaffold/AGENTS.md` | Keep local test nuance. |
| AOT-007 | `src/ThroughlineBuild.Workers.ClaudeCode/AGENTS.md:13-15` | Keep parsing AOT-safe and route final output through shared `WorkerResultParser`; facade owns public API. | `src/ThroughlineBuild.Workers.ClaudeCode/AGENTS.md` | Keep. |
| AOT-008 | `src/ThroughlineBuild.Workers.Common/AGENTS.md:5-11` | `WorkerResultParser` reverse-scans envelopes, last wins, first complete JSON payload wins, metadata is AOT-safe `Dictionary<string, JsonElement>`, fenced block pre-pass has last-wins semantics. | `src/ThroughlineBuild.Workers.Common/AGENTS.md` | Keep parser contract. |
| AOT-009 | `src/ThroughlineBuild.Workers.Common/AGENTS.md:20-22` | Do not replace hand-rolled AOT-safe MarkdownRenderer with a reflection-based library; AOT coverage lives in local tests and Claude worker tests. | `src/ThroughlineBuild.Workers.Common/AGENTS.md` | Keep local warning. |

## Documentation And Generated Help

| ID | Source | Summary | Destination | Duplicate handling |
| --- | --- | --- | --- | --- |
| DOC-001 | `AGENTS.md:27-40` | Read current docs before historical snapshots; source and generated help are authoritative; if changing documented verb/config/contract/status surface, update affected current reference. | `AGENTS.md` | Keep root. |
| DOC-002 | `src/AGENTS.md:22-23` | Trust code over docs; state-of-system is historical; architecture doc describes current tree. | `AGENTS.md` and `src/AGENTS.md` | May shorten nested to current-vs-historical reminder. |
| DOC-003 | `src/ThroughlineBuild.Cli/AGENTS.md:15-21` | Help subsystem owns tiered help; `CliUsage.cs` is legacy tests-only; `models` and `sweep` are not in help registry; adding verbs requires registry/dispatch and help entry when applicable. | `src/ThroughlineBuild.Cli/AGENTS.md` | Keep local. |
| DOC-004 | `run-backlog/SKILL.md:22-23` | `build --help` and `build <command> --help` are command contracts; `build help <topic>` is prose. | `run-backlog/SKILL.md` | Keep. |
| DOC-005 | `run-backlog/references/repo-adaptation.md:59-61` | Use current `build init --print-template` and command help for syntax, not `build help config` as full schema. | `repo-adaptation.md` | Keep adaptation detail. |

## ASCII, Line Endings, Embedded Resources, Snapshots

| ID | Source | Summary | Destination | Duplicate handling |
| --- | --- | --- | --- | --- |
| TXT-001 | `AGENTS.md:75-79` | Write ASCII only in code, comments, docs, commit messages, and ticket bodies; avoid em/en dashes and curly quotes; route existing non-ASCII bodies through files rather than shell variables. | `AGENTS.md` | Keep root. |
| TXT-002 | `tests/AGENTS.md:22-25` | Brief snapshot infrastructure is LF-pinned; template changes need snapshot updates; Fable stream-json fixtures live in Claude worker tests. | `tests/AGENTS.md` | Keep test-local. |
| TXT-003 | `src/ThroughlineBuild.Briefs/Templates/AGENTS.md:11-13` | Template markdown files are embedded resources; edits need rebuild; new files are picked up automatically. | `src/ThroughlineBuild.Briefs/Templates/AGENTS.md` | Keep. |
| TXT-004 | `src/ThroughlineBuild.Briefs/Templates/AGENTS.md:15-17` | `implement.md` forbids `git stash`; `review.md` is git read-only with no stash/checkout/reset/rebase; keep these bans. | `src/ThroughlineBuild.Briefs/Templates/AGENTS.md` | Keep. |
| TXT-005 | `src/ThroughlineBuild.Briefs/Templates/AGENTS.md:19-22` | Template files are LF-pinned; snapshot tests compare exact bytes; edit as LF, run Briefs tests, update snapshots only when intended. | `src/ThroughlineBuild.Briefs/Templates/AGENTS.md` | Keep. |
| TXT-006 | `src/ThroughlineBuild.Scaffold/AGENTS.md:15-19` | Scaffold embeds out-of-tree op-doc spec docs; editing guide changes binary behavior only after rebuild. | `src/ThroughlineBuild.Scaffold/AGENTS.md` | Keep. |
| TXT-007 | `src/ThroughlineBuild.Scaffold/AGENTS.md:21-24` | Scaffold templates are embedded resources and LF-pinned; edit as LF and rebuild; local tests include reflection-off pattern. | `src/ThroughlineBuild.Scaffold/AGENTS.md` | Keep. |
| TXT-008 | `src/ThroughlineBuild.Verification/AGENTS.md:24-25` | Ratify-obsolete prompt is embedded and LF-pinned; rebuild after edits and edit as LF. | `src/ThroughlineBuild.Verification/AGENTS.md` | Keep. |
| TXT-009 | `src/ThroughlineBuild.Workers.Common/AGENTS.md:17-18` | Child process stdout/stderr encoding is pinned to UTF-8; without it .NET uses the OEM code page. | `src/ThroughlineBuild.Workers.Common/AGENTS.md` | Keep. |

## Repo And Project Specific Gotchas

| ID | Source | Summary | Destination | Duplicate handling |
| --- | --- | --- | --- | --- |
| RP-001 | `AGENTS.md:3-10` | Root `AGENTS.md` is the tool-agnostic repo contract; agent-specific files point back; nested `AGENTS.md` files add local detail and override inside subtree. | `AGENTS.md` | Keep root. |
| RP-002 | `AGENTS.md:14-25` | Throughline Build is a .NET 10 native-AOT CLI; deterministic C# owns engine behavior and worker subprocesses handle LLM phases; generated output must remain stack-agnostic, with stack knowledge in derived data rather than engine code. | `AGENTS.md` | Keep root. |
| RP-003 | `AGENTS.md:68` | Read before writing; do not propose or edit code you have not read. | `AGENTS.md` | Keep root. |
| RP-004 | `AGENTS.md:90-93` | Solution file is source of truth; unlisted `src/` dirs are debris; tests are per-project with local fakes. | `AGENTS.md` | Keep root; nested may cross-ref. |
| RP-005 | `AGENTS.md:150-153` | Worker-spawning verbs `build chain`, `implement`, `review`, and `plan` do not nest inside agent sessions; agents should run deterministic verbs directly and leave worker-spawning verbs to plain terminals. | `AGENTS.md` | Keep root. |
| RP-006 | `src/AGENTS.md:3-16` | `src/` contains 20 projects with dependency order; solution file defines real projects and untracked extra dirs are debris. | `src/AGENTS.md` | Keep local orientation; cross-ref root for rule. |
| RP-007 | `tests/AGENTS.md:14-20` | Test doubles are per-project, not shared; reuse project-local fakes; some suites silence diagnostics globally; Phases tests use injected writers and must not restore process-global console redirection. | `tests/AGENTS.md` | Keep. |
| RP-008 | `src/ThroughlineBuild.Cli/AGENTS.md:3-14` | CLI entrypoint, app, registry, bootstrap, arg prepasses, configured verbs, ticket-facing surface, JSON support, and unknown-token exit behavior have specific owners. | `src/ThroughlineBuild.Cli/AGENTS.md` | Keep local orientation. |
| RP-009 | `src/ThroughlineBuild.Cli/AGENTS.md:23-27` | `init`, `settarget`, `user-guide`, `op-doc`, and `models refresh` run before config; worker wiring belongs in `WorkerAgentBuilder`/factory, not `Program.cs`; `ChainPhaseComposition` and exit mapper own seams. | `src/ThroughlineBuild.Cli/AGENTS.md` | Keep. |
| RP-010 | `src/ThroughlineBuild.Contracts/AGENTS.md:3-18` | Contracts project is pure abstractions only: no I/O, process spawning, HTTP, or file access; keep it leaf; `WorkspaceSchema` is the data-carrying exception and canonical state/label set shared by Plane and setup. | `src/ThroughlineBuild.Contracts/AGENTS.md` | Keep. |
| RP-011 | `src/ThroughlineBuild.Phases/AGENTS.md:3-14` | Phase/orchestration classes own lifecycle; `BatchCommitVerifier` re-derives commit attribution from git and must never trust worker-reported SHAs; dependency graph normalizes IDs and relation edges. | `src/ThroughlineBuild.Phases/AGENTS.md` | Keep. |
| RP-012 | `src/ThroughlineBuild.Phases/AGENTS.md:15-35` | Chains are serial in one integration worktree/branch; child branches cut in place; batch implement only leaf children; rework cap is 2; environmental failures skip siblings; ShipPhase pushes target after hygiene; Plane writes use policy except hard lifecycle/resume markers; phase code uses injected writers, not Console, to keep structured stdout clean. | `src/ThroughlineBuild.Phases/AGENTS.md` | Keep. |
| RP-013 | `src/ThroughlineBuild.Plane/AGENTS.md:3-7` | `PlaneTicketingClient` is the sole ticketing implementation and project discovery/provisioning/connectivity owner; no GitHub/Linear adapter exists; `ProjectResolver` resolves/creates projects from raw credentials pre-config. | `src/ThroughlineBuild.Plane/AGENTS.md` | Keep. |
| RP-014 | `src/ThroughlineBuild.Plane/AGENTS.md:8-24` | Plane issue snapshot cache is paginated once and write-through on mutations; keep write-through on new mutations; request throttle and Polly retry 429/5xx with Retry-After; transport retry uses fresh requests/throttle and exhausted failures become environmental; state/label maps are cached from `WorkspaceSchema`; relation cache invalidates source/target on CLI mutations. | `src/ThroughlineBuild.Plane/AGENTS.md` | Keep. |
| RP-015 | `src/ThroughlineBuild.Scaffold/AGENTS.md:3-13` | Scaffold parser is hand-rolled/line-oriented with gathered errors; validator warnings block unless accepted; dry-run makes zero API calls; LLM profile emits `PROJECT_PROFILE` under `WORKER_RESULT` but parsing remains deterministic and writer routing is explicit. | `src/ThroughlineBuild.Scaffold/AGENTS.md` | Keep. |
| RP-016 | `src/ThroughlineBuild.Workers.ClaudeCode/AGENTS.md:3-15` | Claude worker owns transport internals; config defaults to interactive-hook with print rollback; interactive runs use ConPTY/job object or PTY/process group; preflight requires Claude CLI 2.1.177+ and never falls back silently; trust/run-directory/worktree-lock state is outside repo except gitignored `.build/brief.md`; parsing routes through shared parser. | `src/ThroughlineBuild.Workers.ClaudeCode/AGENTS.md` | Keep. |
| RP-017 | `run-backlog/SKILL.md:126-131` | Final reports include ticket final state, exact gates/pass counts, files/commits, blockers, reviewer independence limits, worktree cleanup, clean/dirty tree; installation/adaptation also reports skill/archive parity and confirms no real ticket mutation. | `run-backlog/SKILL.md` | Keep. |
| RP-018 | `run-backlog/agents/openai.yaml:1-4` | UI metadata presents run-backlog as safe backlog implementation/review and says Build-backed fan-out is only for declared/configured repos. | `agents/openai.yaml` | Keep or update only if SKILL wording changes materially. |
