# Cleanup: project-bootstrap onboarding gaps (follow-up to op-33)

> FORMAT NOTE: This is a detailed remediation work-list, NOT a scaffold-format op-doc.
> Do not run `build scaffold` against it. It is meant to be handed to one agent and
> worked top to bottom as a single large cleanup effort. Each work item (WI-NN) is
> self-contained: symptom, root cause with file:line, expected behavior, step-by-step
> implementation guidance, acceptance criteria, and tests.

## Background

op-33 ("project-bootstrap", see [op-33-project-bootstrap.md](op-33-project-bootstrap.md))
shipped seven tickets (TLB-481, 482, 484, 485, 486, 487, 489) to make `build init` a
one-shot, name-based project bootstrap. The code shipped and its offline behavior is
well tested, but operator testing surfaced a gap between what op-33 *specified* and what
the operator *expected*, plus one real output bug. The headline value of op-33 (resolve
or create the Plane project, write the resolved id, provision, welcome commit,
connectivity summary, scaffold pointer) lives entirely behind a "connected mode" that is
only reachable non-interactively by passing `--project-name` or `--from <file>`. An
operator running `build init` at a terminal never reaches it, still gets prompted to
paste a raw project UUID, and several downstream commands then fail in confusing ways.

### What the operator actually experienced (two real sessions)

Session 1 (typo, no project name):
```
$ build init --plane-url https://plane.example.com --workplace throughline --token plane_api_...
Plane workspace slug: throughline        <- prompted despite passing --workplace
Plane project ID:                          <- prompted for a raw UUID (the thing op-33 set out to kill)
Created .../survey-smoketest4/.build/config.toml
Fill in the REQUIRED fields before running other build commands.
```
`--workplace` is not a flag (it is `--workspace`); it was silently dropped, so init
prompted for the workspace, then for a raw project id. No project name was given, so
connected mode never triggered.

Session 2 (correct flags, still no project name):
```
$ build init --plane-url https://plane.example.com --workspace throughline --token plane_api_...
Plane project ID: doode                    <- operator typed a bogus id at the UUID prompt
Created .../survey-smoketest4/.build/config.toml

$ build setup
Local repo:
  git: initialized empty repository
  .gitignore: added 12 entr(ies): ...
Command 'setup' failed: Plane API 404: {"error": "Page not found."}

$ build scaffold op-docs/01-survey-site.md --accept-warnings
Scaffolding op-docs/01-survey-site.md ...
Created plan A: ? "Plan A: Survey-taking core"
  Created brief: ? "vite-scaffold" (parent: ?)
  ... 8 briefs + 2 plans, all "?" ...
Failures:
  [connectivity_check] Plane project connectivity failed for workspace 'throughline' project 'doode' (Plane API returned 404: {"error": "Page not found."}).
```
The project was never created (connected init never ran). `doode` is a literal,
non-existent project id, so every Plane call 404s. Worst of all, scaffold printed ten
"Created ..." lines for tickets it never created.

### The operator's expected onboarding (the north star for this cleanup)

> Put in the Plane URL first, then the workspace slug, then the API key. After the key,
> a back-and-forth: do you want to create a new project, or pull in an existing one? If
> existing, show a list of all projects (most-recently-used first), pick one, and the
> GUID gets filled in for you. If new, ask for a name and an identifier (e.g. `ST` for
> smoketest). Then it should connect, provision, commit, and confirm. No pasting GUIDs.

That guided flow does not exist today and was never specified in op-33; op-33 chose a
non-interactive, name-only resolve-or-create driven by flags or a creds file. Closing
that gap is the bulk of this cleanup (WI-07).

## Recommended execution order

Work top to bottom. The order respects dependencies and front-loads the cheap,
high-value fixes:

1. WI-01  Scaffold output bug (standalone, ship today)
2. WI-02  Actionable Plane "project not found" errors
3. WI-03  Reject unknown flags on `build init`
4. WI-04  Offline init prints accurate next steps
5. WI-05  Welcome commit on the `build setup` / fresh-repo path
6. WI-06  Paginate + harden project listing (unblocks WI-07)
7. WI-07  Interactive guided connected init (the big one; depends on WI-06)
8. WI-08  Broaden/document op-doc detection paths (minor)

Each item is independently shippable except WI-07, which depends on WI-06.

---

## WI-01: Scaffold must not print "Created" for tickets it never created

- Type: Bug
- Priority: P0 (clean, isolated, ship first)
- Files: [src/ThroughlineBuild.Commands/ScaffoldCommand.cs](../../src/ThroughlineBuild.Commands/ScaffoldCommand.cs), [src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs](../../src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs), [tests/ThroughlineBuild.Commands.Tests/](../../tests/ThroughlineBuild.Commands.Tests/)

### Symptom
On a connectivity failure (or any full failure), `build scaffold` prints a full tree of
`Created plan X: ?` and `Created brief: ? (parent: ?)` lines for every plan and brief in
the op-doc, even though zero tickets were created, followed by a single `Failures:` entry.
It reads as "it tried all of them and they half-worked," when in fact nothing was created.

### Root cause
The engine already fails fast and correctly: `ScaffoldPhase.RunAsync` runs the
connectivity check at step 5 ([ScaffoldPhase.cs:116-133](../../src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs#L116-L133))
*before* any creation, and on failure returns immediately with `PlansCreated: 0`,
`BriefsCreated: 0`, and an empty `CreatedTicketIds`. Only the probe call was made; the
ten creates were never attempted.

The bug is purely in the CLI presentation. `BuildCreateOutput`
([ScaffoldCommand.cs:205-246](../../src/ThroughlineBuild.Commands/ScaffoldCommand.cs#L205-L246))
builds its report by walking the parsed op-doc (the *intent*) rather than
`result.CreatedTicketIds` (the *actuals*):
```csharp
string planId = idIndex < result.CreatedTicketIds.Count
    ? result.CreatedTicketIds[idIndex++]
    : "?";
sb.AppendLine($"Created plan {plan.Id}: {planId} ...");   // printed even when nothing was created
```
With `CreatedTicketIds` empty, every id resolves to `"?"` and every "Created" line is a lie.

The exit code is already correct: full failure with all-backend-unavailable failures maps
to `EXIT:4` ([ScaffoldCommand.cs:263-285](../../src/ThroughlineBuild.Commands/ScaffoldCommand.cs#L263-L285)),
so scripts catch it. Only the human-readable output is wrong.

### Expected behavior
- When nothing was created (full failure), print NO creation tree. Print one clear abort
  line naming the cause, e.g.:
  `Scaffold aborted: cannot reach Plane project 'doode' in workspace 'throughline' (404). Nothing was created. Fix plane_project_id in .build/config.toml and retry.`
- Only ever print `Created <id> "<name>"` for ids that are actually present in
  `result.CreatedTicketIds`. Never emit `Created ... ?`.
- Partial creation must still work: if the op ticket and plan A were created but a later
  brief failed, the genuinely-created tickets are still listed by their real ids, the
  failures are listed, and the `Scaffold partial:` summary line is printed as today.

### Implementation steps
1. In `BuildCreateOutput`, stop using `"?"` as a fallback. Restructure so the printed
   creation lines are driven by what is in `result.CreatedTicketIds` (correlated back to
   plan/brief names), not by iterating the op-doc unconditionally. A clean approach:
   thread the created id back per entity rather than re-deriving by positional index.
   Consider having `ScaffoldPhase` return a structured per-entity result (op/plan/brief
   with its created id or null) instead of a flat `CreatedTicketIds` list that the CLI
   has to re-correlate by index; the index correlation is already fragile (see the
   off-by-one comment at [ScaffoldCommand.cs:216-221](../../src/ThroughlineBuild.Commands/ScaffoldCommand.cs#L216-L221)).
   - Lower-effort alternative if you do not want to change the phase contract: when
     `result.PlansCreated == 0 && result.BriefsCreated == 0`, skip the tree entirely and
     emit only the abort line + failures. Then for the partial/success path, guard each
     "Created" line on the id actually existing.
2. Add an explicit "aborted, nothing created" branch with a message that names the
   failing stage and the remedy. Reuse `IsBackendUnavailableFailure`
   ([ScaffoldCommand.cs:288-295](../../src/ThroughlineBuild.Commands/ScaffoldCommand.cs#L288-L295))
   to tailor wording for connectivity vs. other failures.
3. Keep the exit-category tags unchanged (`EXIT:3` partial, `EXIT:4` backend-unavailable,
   `EXIT:2`/`EXIT:0` otherwise).

### Acceptance criteria
- [ ] On a connectivity-failure scaffold, output contains a single clear abort line and
      the failure detail, and contains NO `Created` lines and NO `?` placeholders
- [ ] Process exits non-zero (EXIT:4) on the connectivity-failure case (unchanged)
- [ ] A successful scaffold prints `Created <real-id> "<name>"` for every ticket and a
      `Scaffold complete: N plan(s), M brief(s) created.` line
- [ ] A partial scaffold (some created, then a failure) lists only the real created ids,
      lists the failures, and prints `Scaffold partial: ...`
- [ ] No code path can emit the literal string `Created` for a ticket whose id is unknown

### Tests
- Unit-test `BuildCreateOutput` (or the phase+command together with a fake `ITicketing`
  whose `TestConnectivityAsync` returns failure) asserting the abort message and the
  absence of any `Created`/`?` text.
- Unit-test the partial case (fake ticketing that creates the op + plan A then throws on
  the first brief) asserting only real ids are printed and the partial summary appears.
- Unit-test the all-success case asserting real ids and the complete summary.

### Out of scope
- Changing scaffold's fail-fast policy (it already aborts before creating on connectivity
  failure; that is correct).

---

## WI-02: Replace opaque Plane 404s with an actionable "project not found" error

- Type: Polish / error-message quality
- Priority: P1
- Files: [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs), [src/ThroughlineBuild.Cli/SetupCommand.cs](../../src/ThroughlineBuild.Cli/SetupCommand.cs)

### Symptom
With a wrong or non-existent `plane_project_id`, commands fail with
`Plane API 404: {"error": "Page not found."}` (from `build setup`) or
`... project 'doode' (Plane API returned 404 ...)` (from scaffold connectivity). The
setup message in particular gives the operator nothing to act on.

### Root cause
`build setup` -> `RunPlaneAsync` -> `ListStatesAsync` hits
`api/v1/workspaces/{slug}/projects/{projectId}/states/`. When the project id is bogus,
Plane returns 404 and the raw `PlaneApiException` bubbles up unmapped. There is no
project-existence check and no message that says "this project id is wrong or the project
was never created." `TestConnectivityAsync`
([PlaneTicketingClient.cs:124-151](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L124-L151))
maps 401/403 nicely but folds a 404 into the generic catch.

### Expected behavior
- A 404 on a project-scoped route surfaces as: the configured `plane_project_id` does not
  resolve to a project in this workspace; likely causes are a wrong id or a project that
  was never created; remedy is to re-run init connected mode (or fix `plane_project_id`).
- `build setup` should fail with this actionable message, not the raw `Page not found.`

### Implementation steps
1. In `TestConnectivityAsync`, add a `catch (PlaneApiException ex) when (ex.Status == 404)`
   branch returning a `TicketingConnectivityResult(false, ...)` that names the workspace
   and project id and states the likely cause + remedy.
2. For `build setup`: either (a) have `SetupCommand.RunPlaneAsync` call
   `TestConnectivityAsync` first and surface its message, or (b) wrap the
   `ListStatesAsync`/`ListLabelNamesAsync` calls so a 404 maps to the same actionable
   message before it bubbles to `Program.cs`'s generic `Command 'setup' failed: ...`.
3. Keep wording ASCII-only.

### Acceptance criteria
- [ ] `build setup` against a config with a non-existent project id prints a message that
      names the project id and tells the operator it is wrong or uncreated, with a remedy
- [ ] `build scaffold` connectivity failure on a 404 carries the same actionable wording
- [ ] 401/403 behavior is unchanged (still names the missing scope)

### Tests
- Unit-test `TestConnectivityAsync` with a fake transport returning 404 on the first GET,
  asserting the message names the project id and the remedy.

---

## WI-03: `build init` must reject unknown / misspelled flags

- Type: Bug / UX
- Priority: P1
- Files: [src/ThroughlineBuild.Cli/Program.cs](../../src/ThroughlineBuild.Cli/Program.cs) (init dispatch, lines 231-269), [src/ThroughlineBuild.Cli/CliArgParser.cs](../../src/ThroughlineBuild.Cli/CliArgParser.cs)

### Symptom
`build init --workplace throughline ...` silently dropped `--workplace` (the flag is
`--workspace`) and fell through to prompting. The operator believed they had supplied the
workspace. This single silent-drop is the root cause of the first confusing session.

### Root cause
Init parses flags via `CliArgParser.GetFlagValue`
([CliArgParser.cs:170-178](../../src/ThroughlineBuild.Cli/CliArgParser.cs#L170-L178)),
which only *pulls* the flags it knows about and ignores everything else. There is no
"unknown flag" validation for the init verb, so a typo'd flag is indistinguishable from
not passing it.

### Expected behavior
- `build init` with an unrecognized `--flag` exits non-zero (usage error, exit 2) with a
  clear message, e.g. `Error: unknown flag for 'build init': --workplace` and a pointer to
  the recognized flags / `build --help`.
- Recognized init flags: `--force`, `--print-template`, `--from`, `--plane-url`,
  `--workspace`, `--project-id`, `--project-name`, `--token`, `--token-env`, plus any new
  interactive flags added by WI-07.
- Flag *values* (the token after a flag) must not be mistaken for flags.

### Implementation steps
1. In the init dispatch block in `Program.cs`, after extracting the known flags, scan
   `filteredArgs` for any leftover token that begins with `--` and is not in the
   recognized set (and is not a known flag's value). If found, print the unknown-flag
   error and return 2. Mirror the pattern already used by `op-doc spec`
   ([Program.cs:296-314](../../src/ThroughlineBuild.Cli/Program.cs#L296-L314)) which builds
   a recognized-token set and rejects anything else.
2. Be careful to treat `--flag value` pairs correctly so a value is not flagged as unknown.
3. Update `CliUsage` only if the recognized-flag list changes.

### Acceptance criteria
- [ ] `build init --workplace x` exits 2 with `unknown flag` naming `--workplace`
- [ ] `build init --workspace x --plane-url y` (all recognized) still works
- [ ] A flag value that happens to look like a word is not misreported as an unknown flag
- [ ] Exit code is 2 (usage), distinct from runtime failures (1)

### Tests
- Unit/integration test the init dispatch (or a small extracted validator) for: unknown
  flag rejected; all-known accepted; flag-with-value not misclassified.

---

## WI-04: Offline `build init` prints accurate, complete next steps

- Type: UX
- Priority: P2
- Files: [src/ThroughlineBuild.Cli/InitCommand.cs](../../src/ThroughlineBuild.Cli/InitCommand.cs) (offline tail, lines 151-179)

### Symptom
After offline init, the closing message is:
```
Fill in the REQUIRED fields before running other build commands.
Run 'build user-guide' to write the operator setup guide to docs/.
```
It never mentions `build setup` (the required provisioning step), and never hints that
passing `--project-name` (or using connected/interactive mode) would have done the whole
bootstrap and avoided the manual GUID. The operator is left to discover setup on their own.

### Root cause
The offline tail ([InitCommand.cs:176-178](../../src/ThroughlineBuild.Cli/InitCommand.cs#L176-L178))
hard-codes only the fill-in + user-guide hints.

### Expected behavior
- Offline init that wrote a template with unresolved REQUIRED fields should:
  - point at `build setup` as the next step after the config is filled in, and
  - tell the operator that re-running with `--project-name <name>` (plus url/workspace/token)
    or the interactive mode (WI-07) resolves or creates the project and provisions in one
    shot, so they do not have to paste a UUID.
- If the config still contains `REQUIRED_PLANE_PROJECT_ID`, say so explicitly.

### Implementation steps
1. Detect whether the written `content` still contains any `REQUIRED_` placeholder and
   tailor the message accordingly.
2. Add the `build setup` pointer and the connected-mode hint. Keep it short and ASCII.
3. Do not print these hints on `--print-template`.

### Acceptance criteria
- [ ] Offline init output names `build setup` as a next step
- [ ] Offline init output mentions `--project-name` / connected mode as the one-shot path
- [ ] When a REQUIRED placeholder remains, the message says which field(s) still need values
- [ ] `--print-template` output is unchanged (no hints)

### Tests
- Extend `InitCommandTests` offline cases to assert the new pointers appear, and that
  `--print-template` does not include them.

---

## WI-05: A fresh repo gets the welcome commit even via `build setup` (not only connected init)

- Type: Gap
- Priority: P1
- Files: [src/ThroughlineBuild.Cli/SetupCommand.cs](../../src/ThroughlineBuild.Cli/SetupCommand.cs), [src/ThroughlineBuild.Cli/LocalRepoSetup.cs](../../src/ThroughlineBuild.Cli/LocalRepoSetup.cs), [src/ThroughlineBuild.Cli/InitCommand.cs](../../src/ThroughlineBuild.Cli/InitCommand.cs)

### Symptom
After `build init` (offline) + `build setup`, `git status` shows "No commits yet" with
`.gitignore` and other files untracked. op-33's welcome commit (TLB-486) only runs inside
connected init, so the offline-init-then-setup path - the path the operator actually used -
leaves a repo with zero commits. The first `build ship` from that repo will hit the
no-commit base-ref failure that op-33 set out to eliminate.

### Root cause
`SetupCommand.RunLocalRepo` does `git init` and writes `.gitignore`
([SetupCommand.cs:44-84](../../src/ThroughlineBuild.Cli/SetupCommand.cs#L44-L84)) but never
commits. The welcome-commit logic lives only in `InitCommand.RunConnectedAsync` phase 3.5
([InitCommand.cs:287-307](../../src/ThroughlineBuild.Cli/InitCommand.cs#L287-L307)), which
the offline path never reaches.

### Expected behavior
- After `build setup` initializes a fresh repo (no commits yet), it makes the same
  "welcome to throughline build" commit of `.gitignore` (and only `.gitignore`; never
  `.build/config.toml`), guarded by the no-existing-commits check.
- A repo that already has commits is left untouched.
- Failure to commit (e.g. missing git user.name/email) is a clear non-fatal warning, as in
  connected init.
- The welcome-commit logic should live in one place and be called from both connected init
  and `build setup` (do not duplicate it).

### Implementation steps
1. Extract the welcome-commit block from `InitCommand.RunConnectedAsync` into a shared
   helper (e.g. a method on a small helper class, or a static taking `ILocalRepoOps` +
   `IConsole`) that: checks `HasAnyCommits()`, calls
   `StageAndCommit([".gitignore"], "welcome to throughline build")`, prints the same
   success line, and handles failure as a non-fatal warning.
2. Call the helper from `SetupCommand` after the local-repo provisioning (only in
   non-checkOnly mode), and have connected init call the same helper.
3. Reuse the existing `ILocalRepoOps.HasAnyCommits` / `StageAndCommit`
   ([LocalRepoSetup.cs:139-195](../../src/ThroughlineBuild.Cli/LocalRepoSetup.cs#L139-L195)).
   `SetupCommand` already holds an `ILocalRepoOps`.
4. `build setup --check` must NOT commit (it mutates nothing); only the real run commits.

### Acceptance criteria
- [ ] `build init` (offline) followed by `build setup` on a fresh repo yields a repo where
      `git rev-parse HEAD` succeeds
- [ ] The welcome commit contains `.gitignore` and not `.build/config.toml`
- [ ] A repo with existing commits receives no second bootstrap commit from `build setup`
- [ ] `build setup --check` makes no commit
- [ ] Connected init still makes exactly one welcome commit (no double-commit when both
      paths run)
- [ ] Commit failure (no git identity) is a non-fatal warning, not a hard error

### Tests
- Add `SetupCommand` tests with a fake `ILocalRepoOps` asserting: fresh repo -> one commit
  of `.gitignore`; existing repo -> no commit; `--check` -> no commit.
- Verify the connected-init welcome-commit tests in `InitCommandTests` still pass after the
  extraction.

### Out of scope
- Committing operator source files (only `.gitignore`).
- Pushing the commit anywhere (local only).

---

## WI-06: Paginate and harden workspace project listing

- Type: Correctness
- Priority: P1 (prerequisite for WI-07's picker and for reliable find-or-create)
- Files: [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs), [src/ThroughlineBuild.Plane/PlaneApiModels.cs](../../src/ThroughlineBuild.Plane/PlaneApiModels.cs), [src/ThroughlineBuild.Contracts/IProjectDiscovery.cs](../../src/ThroughlineBuild.Contracts/IProjectDiscovery.cs)

### Symptom / risk
`ListProjectsAsync` reads a single response shaped `{ "results": [...] }`
([PlaneApiModels.cs:166-168](../../src/ThroughlineBuild.Plane/PlaneApiModels.cs#L166-L168),
[PlaneTicketingClient.cs:374-380](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs#L374-L380)).
Two latent problems:
1. No pagination. If Plane's workspace-projects endpoint paginates and a workspace has
   many projects, `FindProjectByNameAsync` can fail to find an existing project and then
   `ResolveAsync` will CREATE A DUPLICATE.
2. Unverified response shape. If the endpoint actually returns a bare JSON array `[...]`
   (not a `{results}` wrapper), deserialization yields an empty/!null `Results` and find
   silently returns null for everything - again leading to spurious creates.

The issue endpoints in this same client paginate deliberately (see the per-run snapshot in
the Plane AGENTS notes); the projects path does not.

### Expected behavior
- `ListProjectsAsync` returns ALL projects in the workspace regardless of count, following
  Plane's pagination (cursor or page links) to exhaustion.
- The response-shape assumption is verified against the live API and the model matches
  reality (wrapper vs. bare array; cursor field name).
- `FindProjectByNameAsync` matches case-insensitively across the full set.

### Implementation steps
1. Confirm the live shape of `GET api/v1/workspaces/{slug}/projects/` (wrapper with a
   cursor/next field, or a bare array). Capture a sample response in the ticket notes.
2. If paginated: add the cursor/next-page field(s) to `PlaneProjectList`, register any new
   model in `PlaneJsonContext`
   ([PlaneApiModels.cs:208-211](../../src/ThroughlineBuild.Plane/PlaneApiModels.cs#L208-L211)),
   and loop in `ListProjectsAsync` until exhausted, accumulating `ProjectInfo`s. Follow the
   existing issue-pagination pattern in this client for consistency.
3. If a bare array: adjust the model/deserialization accordingly (still source-gen, no
   reflection).
4. Keep the project-less routing (works with empty `ProjectId`) intact.

### Acceptance criteria
- [ ] `ListProjectsAsync` returns every project across multiple pages (verified against a
      workspace with more than one page, or by a fake transport that returns two pages)
- [ ] `FindProjectByNameAsync` finds a project that is NOT on the first page
- [ ] No duplicate-create occurs when the target exists beyond page one
- [ ] New/changed models are in the source-gen JSON context; AOT publish has no new
      trim/AOT warnings
- [ ] Live response shape is documented in the ticket

### Tests
- Unit-test `ListProjectsAsync` with a fake transport returning a paginated sequence,
  asserting all projects are returned and find works across pages. Flip reflection off in
  the test (per tests/AGENTS guidance) so the AOT serialization path is exercised.

---

## WI-07: Interactive guided connected init (create-or-pick, no GUID pasting)

- Type: Feature (the core operator request)
- Priority: P1
- Depends on: WI-06 (full project list), benefits from WI-02/WI-03/WI-04/WI-05
- Files: [src/ThroughlineBuild.Cli/InitCommand.cs](../../src/ThroughlineBuild.Cli/InitCommand.cs), [src/ThroughlineBuild.Cli/IConsole.cs](../../src/ThroughlineBuild.Cli/IConsole.cs), [src/ThroughlineBuild.Plane/ProjectResolver.cs](../../src/ThroughlineBuild.Plane/ProjectResolver.cs), [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs), [src/ThroughlineBuild.Plane/PlaneApiModels.cs](../../src/ThroughlineBuild.Plane/PlaneApiModels.cs), [src/ThroughlineBuild.Contracts/IProjectDiscovery.cs](../../src/ThroughlineBuild.Contracts/IProjectDiscovery.cs), [src/ThroughlineBuild.Contracts/IProjectResolver.cs](../../src/ThroughlineBuild.Contracts/IProjectResolver.cs), [src/ThroughlineBuild.Cli/CliUsage.cs](../../src/ThroughlineBuild.Cli/CliUsage.cs)

### Symptom / gap
The interactive onboarding the operator expected does not exist. Today connected mode is
only reachable non-interactively (via `--project-name`/`--from`), and the interactive TTY
path still asks for a raw `Plane project ID`
([InitCommand.cs:371-405](../../src/ThroughlineBuild.Cli/InitCommand.cs#L371-L405)) - the
exact GUID-paste experience op-33 set out to kill. There is no "create new vs. pick
existing," no project list, and no most-recently-used ordering.

### Target operator flow (TTY / interactive)
When `build init` runs interactively (stdin is a TTY, no `--from`, and the operator has
not supplied a project id):
1. Prompt for `Plane base URL` (skip if `--plane-url` given).
2. Prompt for `Plane workspace slug` (skip if `--workspace` given).
3. Prompt for `Plane API token` (skip if `--token`/`--token-env` given). The token is
   required to continue interactively; if left blank, fall back to writing the offline
   template exactly as today.
4. Connect and ask: `Create a new project or use an existing one? [c/e]`.
   - Existing: fetch the workspace projects (WI-06), sort most-recently-used first, and
     present a numbered menu:
     ```
     Select a project:
       1) Survey Smoketest        (ST)    updated 2026-06-05
       2) Throughline Build       (TLB)   updated 2026-06-01
       3) ...
     Enter number (or 'n' to create new):
     ```
     The operator picks a number; the chosen project's id is filled in automatically. No
     UUID is ever typed or shown as something to copy.
   - New: prompt for `Project name` and `Project identifier` (the short Plane prefix, e.g.
     `ST`). Validate the identifier against Plane's rules (uppercase letters, length
     bounds; reuse/relocate `ProjectResolver.DeriveIdentifier`
     ([ProjectResolver.cs:62-80](../../src/ThroughlineBuild.Plane/ProjectResolver.cs#L62-L80))
     to offer a derived default the operator can accept or override). Create the project
     and use the returned id.
5. Proceed with the existing connected pipeline: substitute the resolved id into the
   config, write it, run setup provisioning, make the welcome commit, verify connectivity,
   print the summary (and the scaffold pointer if a doc is present).

Non-interactive behavior is unchanged: `--project-name` resolves by name (find-or-create),
`--project-id`/file id bypasses resolution, `--from`/redirected-stdin stays non-interactive,
and a blank token still yields the offline template.

### Implementation steps
1. Project listing for the picker depends on WI-06. Add a recency signal: extend
   `PlaneProject`/`ProjectInfo` with a sortable timestamp (Plane `updated_at` is the
   pragmatic proxy for "most recently used"; capture it and sort descending). Register any
   model change in `PlaneJsonContext`. If a better activity signal is cheaply available,
   prefer it, but `updated_at` is acceptable for v1 - note the choice in the ticket.
2. Add an interactive driver in `InitCommand` (or a new small `InteractiveInit` helper)
   that orchestrates the prompt flow above using `IConsole`. Keep all I/O behind `IConsole`
   so it is unit-testable with a scripted fake console (see the existing
   `FakeInteractiveConsole` in `InitCommandTests`).
3. Replace the raw `Plane project ID` prompt for the interactive case. Keep a fallback to
   the old behavior only when the operator cannot/will not connect (no token), or behind an
   explicit escape hatch.
4. Extend `IProjectResolver`/`ProjectResolver` (or add a sibling) to support an explicit
   "create with this name AND this identifier" path, since today `ResolveAsync` derives the
   identifier itself. The picker's "use existing" path returns a known id directly (no
   resolve needed); the "create" path needs name + chosen identifier.
5. Add a `--no-interactive` (or `--yes`) escape hatch and/or rely on `IsInputRedirected`
   so automation never blocks on a prompt. Document precedence: explicit flags/file >
   interactive prompts > offline template.
6. Update `CliUsage` init text to describe the interactive flow and any new flag.
7. Make sure connectivity failures and create failures during the interactive flow surface
   the WI-02 actionable messages and do not leave a half-written config (write config only
   after the id is known, matching the current connected order).

### Acceptance criteria
- [ ] Running `build init` interactively with no project flags prompts URL -> slug -> token,
      then offers create-or-pick, and NEVER prompts for or asks the operator to paste a UUID
- [ ] "Use existing" shows all workspace projects (across pages) ordered most-recently-used
      first, lets the operator pick by number, and writes the chosen project's id into the
      config
- [ ] "Create new" prompts for a name and an identifier (offering a derived default),
      validates the identifier, creates the project, and writes the new id
- [ ] After either path, setup provisioning, the welcome commit, connectivity verification,
      and the summary all run (reusing the existing connected pipeline)
- [ ] Non-interactive paths are unchanged: `--project-name` (find-or-create by name),
      `--project-id`/file id (bypass), `--from`/redirected stdin (no prompts), blank token
      (offline template)
- [ ] Automation never blocks on a prompt (redirected stdin or `--no-interactive`)
- [ ] AOT publish succeeds with no new trim/AOT warnings
- [ ] `build init` help documents the interactive flow

### Tests
- Drive the full interactive flow with a scripted `IConsole` fake plus a fake
  `IProjectDiscovery`/resolver: assert the pick-existing path writes the chosen id and the
  create-new path creates with the entered name+identifier.
- Assert the menu is sorted most-recently-used first.
- Assert blank token falls back to the offline template.
- Assert redirected stdin / `--no-interactive` never prompts.
- Assert no code path prints a UUID as something to copy in the interactive flow.

### Open design questions (resolve in the ticket, do not block)
- Exact recency signal (updated_at vs. a richer activity field).
- Whether to also offer interactive mode when only the token is missing but url/slug are
  passed as flags (recommended: yes, prompt only for the token).
- Whether create-new should also seed states/labels immediately (it already will, via the
  shared setup provisioning step).

---

## WI-08: Broaden or document op-doc detection paths for the scaffold pointer

- Type: Minor / documentation
- Priority: P3
- Files: [src/ThroughlineBuild.Cli/InitCommand.cs](../../src/ThroughlineBuild.Cli/InitCommand.cs) (`FindDocPaths`, lines 353-369)

### Symptom
The operator placed their op-doc at `op-docs/01-survey-site.md` (repo root), but the
post-bootstrap scaffold-pointer detection only scans `docs/op-docs/` and `docs/proposals/`
([InitCommand.cs:353-369](../../src/ThroughlineBuild.Cli/InitCommand.cs#L353-L369)), so the
doc would not be detected. (It did not matter here because scaffold was run manually, but
the convention is narrow and undiscoverable.)

### Expected behavior
- Either broaden detection to also recognize a top-level `op-docs/` directory, OR clearly
  document the two canonical locations so operators put docs where detection looks.
- Whatever the decision, detection must stay bounded to a small set of known directories
  (do not scan arbitrary repo markdown - that was an explicit op-33 non-goal).

### Implementation steps
1. Decide: broaden to include top-level `op-docs/` (and maybe `proposals/`), or document
   the canonical `docs/op-docs` + `docs/proposals` locations in the init summary and
   user-guide.
2. If broadening, add the directories to the `relDirs` list in `FindDocPaths` and add a
   test; keep paths relative with forward slashes.

### Acceptance criteria
- [ ] Either a top-level `op-docs/*.md` is detected and shown with a `build scaffold`
      pointer, or the canonical locations are documented where the operator will see them
- [ ] Detection remains bounded to a fixed directory allow-list (no arbitrary scanning)

### Tests
- If broadening: extend `FindDocPaths` tests for the new directory.

---

## Definition of done (whole cleanup)

- [ ] WI-01 through WI-08 each meet their acceptance criteria
- [ ] `dotnet build -c Release` is clean; `dotnet test -c Release` passes
- [ ] AOT publish of the CLI succeeds with no new trim/AOT warnings
- [ ] The end-to-end manual smoke test below passes against a real Plane workspace

## End-to-end manual smoke test (operator acceptance)

Run in a brand-new empty directory against the real Plane workspace:

1. `build init` (no flags). Confirm it prompts URL -> slug -> token, then offers
   create-or-pick. Choose "create new", enter a name and identifier (e.g. `ST`). Confirm:
   no UUID is ever requested; config is written with the resolved id; git is initialized;
   `.gitignore` is written; states/labels are provisioned; a "welcome to throughline build"
   commit exists (`git rev-parse HEAD` succeeds); a connectivity-OK summary prints.
2. In a second new directory, `build init` again and choose "use existing"; pick the
   project created in step 1 from the most-recently-used list. Confirm the same id is
   written without typing a UUID.
3. Point `plane_project_id` at a bogus value and run `build setup`. Confirm an actionable
   "project not found / wrong id" message (WI-02), not a raw 404.
4. Run `build scaffold <doc>` against that bogus config. Confirm a single clear
   "aborted, nothing created" message with NO `Created ... ?` lines (WI-01), and a non-zero
   exit.
5. `build init --workplace x ...`. Confirm an "unknown flag" usage error (WI-03).
6. Fix the config and run a real `build scaffold`. Confirm only real ticket ids are
   printed and the run reports complete.

## Appendix: file map (where each concern lives)

- init verb dispatch + flag parsing: [src/ThroughlineBuild.Cli/Program.cs](../../src/ThroughlineBuild.Cli/Program.cs) (init block ~231-269), [src/ThroughlineBuild.Cli/CliArgParser.cs](../../src/ThroughlineBuild.Cli/CliArgParser.cs)
- init command (offline + connected + prompts + welcome commit + summary + doc detection): [src/ThroughlineBuild.Cli/InitCommand.cs](../../src/ThroughlineBuild.Cli/InitCommand.cs)
- credentials file parsing: [src/ThroughlineBuild.Cli/CredsFileParser.cs](../../src/ThroughlineBuild.Cli/CredsFileParser.cs)
- config template: [src/ThroughlineBuild.Commands/Templates/config.toml.template](../../src/ThroughlineBuild.Commands/Templates/config.toml.template)
- setup provisioning (git init, .gitignore, states, labels): [src/ThroughlineBuild.Cli/SetupCommand.cs](../../src/ThroughlineBuild.Cli/SetupCommand.cs)
- local git ops + managed .gitignore: [src/ThroughlineBuild.Cli/LocalRepoSetup.cs](../../src/ThroughlineBuild.Cli/LocalRepoSetup.cs)
- project resolver (find-or-create, identifier derivation): [src/ThroughlineBuild.Plane/ProjectResolver.cs](../../src/ThroughlineBuild.Plane/ProjectResolver.cs), [src/ThroughlineBuild.Contracts/IProjectResolver.cs](../../src/ThroughlineBuild.Contracts/IProjectResolver.cs)
- Plane client (list/find/create projects, connectivity, error mapping): [src/ThroughlineBuild.Plane/PlaneTicketingClient.cs](../../src/ThroughlineBuild.Plane/PlaneTicketingClient.cs)
- Plane models + source-gen JSON context: [src/ThroughlineBuild.Plane/PlaneApiModels.cs](../../src/ThroughlineBuild.Plane/PlaneApiModels.cs)
- project discovery contract: [src/ThroughlineBuild.Contracts/IProjectDiscovery.cs](../../src/ThroughlineBuild.Contracts/IProjectDiscovery.cs)
- scaffold engine (fail-fast connectivity + create loop): [src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs](../../src/ThroughlineBuild.Scaffold/ScaffoldPhase.cs)
- scaffold CLI output (the WI-01 bug): [src/ThroughlineBuild.Commands/ScaffoldCommand.cs](../../src/ThroughlineBuild.Commands/ScaffoldCommand.cs)
- usage text: [src/ThroughlineBuild.Cli/CliUsage.cs](../../src/ThroughlineBuild.Cli/CliUsage.cs)
- existing tests to extend: [tests/ThroughlineBuild.Cli.Tests/InitCommandTests.cs](../../tests/ThroughlineBuild.Cli.Tests/InitCommandTests.cs), [tests/ThroughlineBuild.Cli.Tests/CredsFileParserTests.cs](../../tests/ThroughlineBuild.Cli.Tests/CredsFileParserTests.cs), [tests/ThroughlineBuild.Plane.Tests/ProjectResolverTests.cs](../../tests/ThroughlineBuild.Plane.Tests/ProjectResolverTests.cs)
