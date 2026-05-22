# State of the System - Repo Documentation Prompt (generic)

A drop-in prompt for producing a code-true "state of the system" doc set for
any repository. Fill the CONFIG block, then paste CONFIG + PROMPT into a
session opened at the repo root.

The agent produces the doc set **and** a `PROMPT.md` that records exactly
what it was given, so the set stays reproducible and updatable on later runs.

---

## How to use

1. Fill the CONFIG block. Delete any line that does not apply.
2. Paste the CONFIG block followed by the PROMPT body into the session.
3. On a later refresh, paste the same (or amended) CONFIG + PROMPT. The agent
   reads the existing `PROMPT.md`, updates docs in place, and appends to the
   refresh history. If the prompt text itself changed, it updates the verbatim
   copy in `PROMPT.md` and notes what changed.

---

## CONFIG

```
Repo:            latticeflow
Main focus:      "even coverage, no single focus"
Related sets:    "none"
Must-answer Qs:  "none, derive them"
Output dir:      docs/state-of-the-system/
Notify on done:  notify me
```

---

## PROMPT

Read through the entire `<Repo>` repository as it exists today and document it
thoroughly: what is in it, what each component does, what each reads and
writes, what each expects from the workspace and from the host machine, and
how it composes with the rest of the stack.

Where a main focus is named in CONFIG, that subsystem gets the deepest
treatment - it is the load-bearing surface and the rest of the set is context
around it. Where CONFIG says even coverage, weight the docs by how much each
area actually matters to someone re-scaffolding the system.

Write it for where the repo is **today**, broken into logical sections.

### Rules

- **Code-true.** Read from source, not from existing docs or specs. Where a
  doc and the code disagree, the code wins, and note the disagreement.
- **Cite `file:line` for every claim.** No assertion about behavior without a
  reference.
- **Point to schemas and contracts, do not reproduce them.** Reference schema
  files, type definitions, and contract files by path. Keep the prose at the
  level of what they mean and how they are used.
- **Status-tag every command and major code path** as one of: Functional,
  Partial, Legacy, Aspirational, Broken.
- **End every section with a "loose ends" call-out** - declared but unused
  capabilities, dead references, planned-but-not-shipped behavior, known gaps.
- If a CONFIG question has no implementation, answer it explicitly as "not
  implemented" and name the boundary where it would live.

### Cross-reference

If CONFIG names related doc sets, mirror their depth and structure - the
reader will have all sets open side by side. Note where this repo's surfaces
connect to theirs.

### Questions the set must answer

Answer every question in the CONFIG must-answer list, each by `file:line`
cite or by explicit "not implemented + boundary named".

In addition, regardless of CONFIG, the set must answer all of the following:

1. **Inventory.** Every command / module / service / endpoint / script: what
   it does at a high level, its inputs (arguments, files read, env vars, MCP
   tools, network calls), its outputs (files written, side effects, exit
   states), and which other components it invokes.
2. **Install / build / run.** How the repo gets onto a machine and runs -
   setup scripts, package managers, build steps. What an update does, what an
   uninstall leaves behind, what the host machine must provide.
3. **External dependencies.** Services, APIs, MCP servers, and databases the
   repo requires, and which specific tools or endpoints from each. What the
   handshake looks like when a dependency is missing or unauthenticated.
4. **Configuration and environment.** Every env var, config file, and secret.
   Which are required vs optional, and which are referenced but unused.
5. **State and persistence.** Everything the repo writes over the lifetime of
   a session - files, directories, logs, scratch state, DB rows, caches -
   where it writes them, and whether they are cleaned up.
6. **Public surfaces.** APIs, CLIs, and exported interfaces other code depends
   on, with the functional state of each.
7. **Contracts with sibling repos or systems.** What this repo reads that
   another wrote, and vice versa. Where two definitions of a shared artifact
   overlap or conflict, and how each side handles the other's version.
8. **Workspace and environment assumptions.** What the code assumes about
   where it runs beyond the obvious - branch conventions, required tooling,
   OS, CI integration, stack-specific code paths in command or build bodies.
9. **Failure modes and idempotency.** For each major operation: how it fails,
   and whether re-running it is safe.
10. **Lifecycle / orchestration.** If the repo drives a multi-step process,
    the phases, the coordination protocol, and how it transitions state.

### Output

Save the docs under the CONFIG output directory (default
`docs/state-of-the-system/`). Use a numbered scheme with a `00`-prefixed
index/README that carries a short architectural map and a one-line summary of
each doc. Standalone-readable documents - one per logical section. Split into
several documents rather than one long file.

### PROMPT.md (required deliverable)

Write a `PROMPT.md` alongside the doc set containing:

- **Verbatim prompt.** The exact CONFIG block and PROMPT body used for this
  run, copied verbatim, in a fenced block. If this is a refresh and the prompt
  text changed since the last run, update this copy and add a short note of
  what was added, removed, or reworded.
- **Document set.** A list of every document in the set with a one-line
  description, and how the set evolved (docs added or removed over time).
- **How the prompt was interpreted.** Any judgment calls made - what "main
  focus" was taken to mean, what was deliberately covered lightly, how
  ambiguous instructions were resolved.
- **Refresh history.** A table: date, branch / HEAD commit, notes on what
  changed in that pass.

On every later run, update `PROMPT.md` in place: refresh the verbatim prompt
if it changed, update the document set list, and append a row to the refresh
history table.

### Voice

Professional. No marketing prose, no "we" voice, no emoji. Plain technical
prose, `file:line` references throughout.

### On completion

<Notify on done - e.g. "notify me when you're done", or omit.>