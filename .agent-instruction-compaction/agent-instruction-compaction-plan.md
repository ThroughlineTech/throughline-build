# Agent Instruction Compaction Plan

## Objective

Reduce context consumed by this repository's `AGENTS.md` instruction tree and the
`run-backlog` skill without changing behavior. The agent executing this plan must
preserve every normative rule, permission boundary, safety constraint, command contract,
and repo-specific gotcha. The desired result is less repeated text, not weaker guidance.

## Scope

Repository files:

```text
AGENTS.md
src/AGENTS.md
tests/AGENTS.md
src/ThroughlineBuild.Briefs/Templates/AGENTS.md
src/ThroughlineBuild.ClaudeCode/AGENTS.md
src/ThroughlineBuild.Cli/AGENTS.md
src/ThroughlineBuild.Contracts/AGENTS.md
src/ThroughlineBuild.Phases/AGENTS.md
src/ThroughlineBuild.Plane/AGENTS.md
src/ThroughlineBuild.Scaffold/AGENTS.md
src/ThroughlineBuild.Verification/AGENTS.md
src/ThroughlineBuild.Workers.ClaudeCode/AGENTS.md
src/ThroughlineBuild.Workers.Common/AGENTS.md
```

Skill files:

```text
${CODEX_HOME:-$HOME/.codex}/skills/run-backlog/SKILL.md
${CODEX_HOME:-$HOME/.codex}/skills/run-backlog/agents/openai.yaml
${CODEX_HOME:-$HOME/.codex}/skills/run-backlog/references/agent-contracts.md
${CODEX_HOME:-$HOME/.codex}/skills/run-backlog/references/parallel-fan-out.md
${CODEX_HOME:-$HOME/.codex}/skills/run-backlog/references/repo-adaptation.md
```

Do not edit code, tests, config, ticket state, branches, or generated docs as part of
this plan. If a behavior-preserving text edit appears to require a code or config change,
stop and report why.

## Non-Negotiables

- Use Git Bash for all shell commands.
- Preserve ASCII-only output in edited files.
- Do not weaken any rule about ticket access, Plane access, shell use, branch safety,
  commits, pushes, merges, deployment, worktree lifecycle, AOT serialization, gates,
  worker boundaries, review independence, or preserving unrelated changes.
- Do not run `build chain`, `build implement`, `build review`, or `build plan` from an
  agent session.
- Do not mutate real backlog tickets while executing this plan.
- Use `apply_patch` or an editor for manual edits; do not rewrite files with ad hoc shell
  redirection.
- After each step, archive the actual post-step contents and write a short analysis before
  beginning the next step.

## Archive Layout

Create all comparison material under:

```text
.agent-instruction-compaction/
```

Use these checkpoint directories:

```text
.agent-instruction-compaction/00-before-step-1/
.agent-instruction-compaction/01-after-rule-inventory/
.agent-instruction-compaction/02-after-root-agents/
.agent-instruction-compaction/03-after-nested-agents/
.agent-instruction-compaction/04-after-skill-router/
.agent-instruction-compaction/05-after-skill-references/
.agent-instruction-compaction/06-after-validation/
```

Each checkpoint must contain:

```text
files/
manifest.sha256
word-counts.txt
analysis.md
```

`files/` must preserve enough path information to compare the actual contents. For repo
files, copy from the repo root. For the external skill, copy under `files/run-backlog/`.

## Checkpoint Command Template

Run this once at the start of every checkpoint, changing `CHECKPOINT` each time:

```bash
set -euo pipefail
REPO_ROOT="$(git rev-parse --show-toplevel)"
cd "$REPO_ROOT"
SKILL_DIR="${CODEX_HOME:-$HOME/.codex}/skills/run-backlog"
CHECKPOINT=".agent-instruction-compaction/00-before-step-1"

mkdir -p "$CHECKPOINT/files/repo" "$CHECKPOINT/files/run-backlog"

while IFS= read -r path; do
  mkdir -p "$CHECKPOINT/files/repo/$(dirname "$path")"
  cp "$path" "$CHECKPOINT/files/repo/$path"
done < <(find . -iname 'AGENTS*.md' -print | sed 's#^\./##' | sort)

cp "$SKILL_DIR/SKILL.md" "$CHECKPOINT/files/run-backlog/SKILL.md"
mkdir -p "$CHECKPOINT/files/run-backlog/agents" "$CHECKPOINT/files/run-backlog/references"
cp "$SKILL_DIR/agents/openai.yaml" "$CHECKPOINT/files/run-backlog/agents/openai.yaml"
cp "$SKILL_DIR"/references/*.md "$CHECKPOINT/files/run-backlog/references/"

(
  cd "$CHECKPOINT/files"
  find . -type f -print0 | sort -z | xargs -0 sha256sum
) > "$CHECKPOINT/manifest.sha256"

find "$CHECKPOINT/files" -type f -print0 \
  | sort -z \
  | xargs -0 wc -w \
  > "$CHECKPOINT/word-counts.txt"
```

Then write `analysis.md` for that checkpoint. It must include:

- what changed since the previous checkpoint;
- word-count delta from the previous checkpoint, if one exists;
- whether any normative behavior changed;
- duplicate rules still remaining;
- decision to proceed, rework, or stop.

Use `diff -ru` between checkpoint `files/` directories for content comparison. Example:

```bash
diff -ru \
  .agent-instruction-compaction/00-before-step-1/files \
  .agent-instruction-compaction/01-after-rule-inventory/files \
  > .agent-instruction-compaction/01-after-rule-inventory/diff-from-previous.patch || true
```

## Step 1 - Rule Inventory

Create a machine-checkable inventory before editing instruction prose.

Actions:

1. Archive `00-before-step-1`.
2. Read every scoped file.
3. Create `.agent-instruction-compaction/rule-inventory.md`.
4. Extract every normative rule using categories:
   - shell and platform;
   - ticket and Plane access;
   - branch, commit, push, merge, deploy;
   - worktree and parallelism;
   - conductor, implementer, reviewer boundaries;
   - gates and validation;
   - AOT and serialization;
   - documentation and generated help;
   - ASCII, line endings, embedded resources, snapshots;
   - repo/project-specific gotchas.
5. For each rule, record:
   - source file and line;
   - current wording summary;
   - canonical destination after compaction;
   - whether duplicates may become cross-references.
6. Archive `01-after-rule-inventory`.
7. Write checkpoint analysis before step 2.

Exit criteria:

- Every `must`, `never`, `do not`, `only`, `require`, and gate instruction in scope is accounted for.
- No behavior edits have been made yet, except adding inventory/archive files.

## Step 2 - Compress Root AGENTS.md

Keep root `AGENTS.md` authoritative while making it shorter.

Actions:

1. Edit only `AGENTS.md`.
2. Preserve:
   - repo identity and stack-agnostic design constraint;
   - current-docs versus historical-docs distinction;
   - required build/test gates and exact commands;
   - branch, commit, ASCII, and shipping rules;
   - AOT/source-generated JSON rule;
   - solution-file source of truth;
   - per-project tests and local fakes;
   - gate-vacuity warning;
   - `build`-only ticket access and no direct Plane/REST/MCP path;
   - configured-backend caveat and `build init` instruction;
   - self-editing-ticket-client hazard;
   - no nested worker-spawning verbs from an agent session.
3. Collapse repeated explanation into shorter bullets.
4. Prefer references to `build --help` / `build <verb> --help` instead of long prose,
   while keeping the key command list needed for safe ticket operations.
5. Archive `02-after-root-agents`.
6. Compare against `01-after-rule-inventory`.
7. Write checkpoint analysis before step 3.

Exit criteria:

- The rule inventory marks every root rule as preserved.
- No nested file or skill file has changed in this step.

## Step 3 - Reduce Nested AGENTS.md To Local Deltas

Nested files should add local detail, not repeat root law.

Actions:

1. Edit only nested repo `AGENTS.md` files.
2. Keep project-specific orientation, ownership boundaries, embedded-resource warnings,
   line-ending requirements, snapshot requirements, and local testing/gotcha notes.
3. Remove or shorten root-level repeats such as generic AOT, solution-file, or test-suite
   rules when the nested file does not add local nuance.
4. Do not remove local nuances:
   - `tests/AGENTS.md`: reflection-disabled AOT-sensitive test pattern and per-project doubles.
   - `src/ThroughlineBuild.Briefs/Templates/AGENTS.md`: LF-pinned templates, snapshot bytes,
     git-state bans in templates.
   - `src/ThroughlineBuild.Plane/AGENTS.md`: cache write-through, throttle/retry behavior,
     transport retry classification, canonical schema, relation cache behavior.
   - `src/ThroughlineBuild.Phases/AGENTS.md`: serial orchestration, rework cap, ship push
     behavior, write policy, explicit writer routing.
   - `src/ThroughlineBuild.Verification/AGENTS.md`: stack-agnostic checks, vacuity proof,
     environmental gate control, obsolete ratifier, tool-enforcement warning.
   - worker/facade/scaffold files: transport ownership, parser ownership, embedded resources,
     and rebuild implications.
5. Archive `03-after-nested-agents`.
6. Compare against `02-after-root-agents`.
7. Write checkpoint analysis before step 4.

Exit criteria:

- Each nested file remains useful when an agent is editing inside that subtree.
- The root file still carries any rule removed from nested files.

## Step 4 - Make run-backlog SKILL.md A Lean Router

`SKILL.md` should carry activation, mode selection, and hard invariants. Detailed procedures
belong in references.

Actions:

1. Edit only `${CODEX_HOME:-$HOME/.codex}/skills/run-backlog/SKILL.md`.
2. Keep:
   - conductor role;
   - repository instructions and declared ticket client are authoritative;
   - shell contract;
   - serial mode default;
   - fan-out only when requested or repo-declared and Build-backed config exists;
   - read `parallel-fan-out.md` before fan-out;
   - read `repo-adaptation.md` before installation/adaptation;
   - conductor-only ownership of ticket mutations, commits, integration, branches,
     and worktree lifecycle;
   - implementer/reviewer use `agent-contracts.md`;
   - preserve unrelated changes;
   - read ticket body and comments;
   - ticket-client self-change mutation caution;
   - no deploy/push/merge/ship without explicit authorization;
   - run branch bootstrap behavior;
   - final report contents.
3. Move detailed serial-loop procedure into a new reference file if needed, for example:
   `${CODEX_HOME:-$HOME/.codex}/skills/run-backlog/references/serial-loop.md`.
4. In `SKILL.md`, replace long duplicated details with short links to the relevant refs.
5. Archive `04-after-skill-router`.
6. Compare against `03-after-nested-agents`.
7. Write checkpoint analysis before step 5.

Exit criteria:

- An agent reading only `SKILL.md` knows which reference to read for serial, fan-out,
  adaptation, and worker contracts.
- No safety invariant exists only in a removed paragraph.

## Step 5 - Deduplicate run-backlog References

References should own their topic once and inherit common rules from `SKILL.md`.

Actions:

1. Edit only run-backlog reference files and `agents/openai.yaml` if a shorter prompt is
   obviously equivalent.
2. Remove repeated Bash setup from `parallel-fan-out.md` and `repo-adaptation.md`; state
   that they inherit the shell contract from `SKILL.md`.
3. Keep exact fan-out command contracts and planner input schema in `parallel-fan-out.md`.
4. Keep exact implementer/reviewer contract text in `agent-contracts.md`; this is allowed
   to remain more verbose because it is pasted to workers.
5. Avoid repeating adapter warnings in both `SKILL.md` and `agent-contracts.md`; keep the
   detailed adapter warning in `agent-contracts.md`.
6. Keep installation/adaptation validation exhaustive in `repo-adaptation.md`, but collapse
   prose that repeats the skill's invariants.
7. Archive `05-after-skill-references`.
8. Compare against `04-after-skill-router`.
9. Write checkpoint analysis before step 6.

Exit criteria:

- Each reference has one clear owner topic.
- Shared invariants are stated once and linked elsewhere.
- Fan-out, adaptation, and worker-contract behavior are unchanged.

## Step 6 - Validation And Final Analysis

Prove the compaction preserved behavior.

Actions:

1. Build a final rule-preservation table in
   `.agent-instruction-compaction/final-rule-preservation.md`.
2. For every rule in `rule-inventory.md`, mark:
   - preserved unchanged;
   - preserved with shorter wording;
   - moved to another file;
   - intentionally duplicated for safety;
   - needs human review.
3. Run targeted searches:

```bash
rg -n "Plane|REST|MCP|/ticket-|build chain|build implement|build review|build plan|PublishAot|JsonSerializerContext|reflection|push|merge|deploy|worktree|reviewer|implementer|Bash|Git Bash" \
  AGENTS.md src tests "$SKILL_DIR"
```

4. Verify links and referenced files exist:

```bash
test -f AGENTS.md
test -f src/AGENTS.md
test -f tests/AGENTS.md
test -f "$SKILL_DIR/SKILL.md"
test -f "$SKILL_DIR/references/agent-contracts.md"
test -f "$SKILL_DIR/references/parallel-fan-out.md"
test -f "$SKILL_DIR/references/repo-adaptation.md"
```

5. Run word-count comparison across all checkpoints and summarize savings:

```bash
for d in .agent-instruction-compaction/[0-9][0-9]-*; do
  printf "%s " "$d"
  awk 'END { print $1 }' "$d/word-counts.txt"
done
```

6. Archive `06-after-validation`.
7. Write final checkpoint analysis and a root summary:
   `.agent-instruction-compaction/summary.md`.

Exit criteria:

- No rule is lost.
- Any intentional duplicate is justified.
- Final word-count savings are reported.
- The working tree status is reported.
- The final summary lists exact files changed and all generated archive/checkpoint files.

## Human Review Questions

Ask before proceeding only if one of these occurs:

- A rule appears contradictory and cannot be preserved without choosing a behavior.
- The run-backlog skill directory cannot be found.
- A referenced `AGENTS.md` file has been changed concurrently in a way that alters the plan scope.
- Compaction would require editing code, tests, `.build/config.toml`, or ticket state.

## Final Report Format

When finished, report:

```text
Instruction compaction complete.

Changed instruction files:
- ...

Generated comparison artifacts:
- .agent-instruction-compaction/...

Behavior-preservation result:
- rules inventoried: N
- preserved unchanged: N
- preserved shorter/moved: N
- intentional duplicates: N
- needs human review: N

Word-count result:
- before: N words
- after: N words
- savings: N words

Validation:
- link/file checks: pass/fail
- targeted search review: pass/fail
- working tree: clean/dirty summary
```
