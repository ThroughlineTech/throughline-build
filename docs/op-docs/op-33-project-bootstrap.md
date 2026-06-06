# Operation: project-bootstrap

Make `build init` a one-shot, name-based project bootstrap so onboarding a repo stops requiring hand-edited TOML and pasted GUIDs. Given workspace credentials and a project name - from flags or a reusable text file - init resolves the Plane project by name, creates it if it is absent, pulls its id for you, writes the config, provisions the local repo and the project's states and labels, makes a welcome commit so the first ship works, and prints a connectivity summary. It also hardens ship so a stray untracked file no longer aborts a merge.

## Why this exists

Onboarding a repo is the highest-friction surface in the whole tool, and the worst part is hand-entering a Plane project UUID. Today `build init` writes a config template and prompts for four required values, one of which is a raw GUID the operator has to hunt down in Plane's UI and paste in by hand. Everything else about the value is stable across a workspace - the base URL, the workspace slug, and the API token never change between projects - yet there is no way to keep those in a file and vary only the one thing that actually differs per repo: the project. Pasting GUIDs in 2026 is a 1982 experience, and it is the first thing a new project asks of the operator.

The friction is structural, not cosmetic, and it recurs on every new project. The Plane client is hard-keyed on a project id as an input, which is correct for the run-time workflow but exactly backwards for bootstrap, where for a brand-new project the id is an output the tool should produce, not a value the operator should supply. `build setup` then leaves the repo with zero commits, so the first `build ship` dies on the no-commit base-ref failure until the operator happens to commit something unrelated. And ship's pre-flight deliberately ignores untracked files, so a single stray file - a generated artifact nobody cares about - sails past the gate and then aborts the rebase or fast-forward merge with an opaque git error. Each of these hits every operator who onboards a project, every time.

The timing is right because the pieces already exist and only need to be connected, named, and sequenced. `build init` already renders the config and accepts the values as flags; `build setup` already does git init, the managed .gitignore, and the states/labels provisioning idempotently; the Plane client already has a connectivity probe. What is missing is the one capability that collapses the GUID problem - resolve a project name to an id, creating it if needed - plus the orchestration that runs the existing steps in one command and ends with a commit and a friendly confirmation. This operation builds that capability, wires the one-shot flow on the verb the operator already reaches for, and fixes the two latent traps (no first commit, untracked-file merge abort) that make a freshly-onboarded repo fail on its first real use.

## Dispatch order

| Plan | Name | Depends on | Effort |
| ---- | ---- | ---------- | ------ |
| A | Plane project resolution | - | M |
| B | One-shot name-based bootstrap | A | M |
| C | Ship robust to untracked files | - | M |

A is the foundation - the name-to-id capability the bootstrap is built on - and is buildable and testable against Plane with no CLI changes. B consumes A to deliver the connected `build init` flow, the credentials file, the welcome commit, and the scaffold handoff. C is independent of both: it fixes the untracked-file merge abort in ship and can land in parallel.

## Plan A: Plane project resolution

### Goal

After this plan, the Plane client can operate at the workspace level with no project id in hand: given only a base URL, workspace slug, and token, it lists the workspace's projects, finds one by name, and creates a new one, returning the resolved id. A small resolver composes those calls into a single "find or create this named project" operation that reports which path it took. Nothing in the CLI uses it yet; this plan delivers the capability the bootstrap consumes.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 01 | workspace-project-list-create | Client methods to list/find/create projects with no project id, plus source-gen models | - | src/ThroughlineBuild.Plane/PlaneTicketingClient.cs, src/ThroughlineBuild.Plane/PlaneApiModels.cs, src/ThroughlineBuild.Plane/PlaneClientOptions.cs |
| 02 | project-resolver-service | A find-or-create resolver constructed from raw creds, reporting found vs created | 01 | src/ThroughlineBuild.Plane/PlaneTicketingClient.cs, src/ThroughlineBuild.Contracts/ITicketingProvisioner.cs, src/ThroughlineBuild.Cli/Config.cs |

### Briefs - detail

#### Brief 01: workspace-project-list-create

Goal: The Plane client can, given only a workspace slug and token, list the workspace's projects, find one by name, and create a new one - returning its id. This is the single capability that lets an operator name a project instead of pasting its UUID, because it turns a name into the id the rest of the system needs.

Inputs: `PlaneTicketingClient.cs` (the `IssuesBase`/`StatesBase`/`LabelsBase` URL builders at lines 182-192, the `GetJsonAsync` helper, the throttle and Polly retry pipeline in the constructor); `PlaneApiModels.cs` and the source-generated `PlaneJsonContext` used at `_jsonOptions`; `PlaneClientOptions.cs` (where `ProjectId` and `ProjectIdentifier` live and `ProjectId` is assumed non-empty today); the Plane REST project routes (`GET`/`POST api/v1/workspaces/{slug}/projects/`).

Outputs:
- New client methods: `ListProjectsAsync`, `FindProjectByNameAsync` (case-insensitive, returns the id or null), and `CreateProjectAsync(name, identifier)` returning the new project's id.
- New request/response models for the projects endpoint, registered in the source-generated `PlaneJsonContext` so no reflection serialization is introduced.
- A project-less call path: these three methods route on the workspace slug alone and do not throw when `ProjectId` is empty, unlike the issue/state/label routes.
- A token lacking workspace-admin scope surfaces as a typed, actionable error on create (401/403), not an opaque failure.

Acceptance:
- [ ] `FindProjectByNameAsync` returns the id for an existing project and null for a missing one, matching case-insensitively
- [ ] `CreateProjectAsync` creates a project and returns its id
- [ ] List and create succeed with an empty `ProjectId` configured
- [ ] A token without create permission yields a clear typed error naming the missing scope
- [ ] AOT publish succeeds; the new models are in the source-gen JSON context with no new trim or AOT warnings

Notes: The whole "stop pasting GUIDs" goal reduces to one capability - name to id - so it belongs on the client that already owns Plane auth, throttling, and retry. A project-less path on the existing client was chosen over a second HTTP client because the throttle, resilience pipeline, and `X-API-Key` header are already correct on `PlaneTicketingClient`; duplicating them into a parallel client would drift over time. Plane requires a short project identifier (the `TLB`-style prefix) at create time, and `ProjectIdentifier` already exists on the options - whether the bootstrap derives it from the name or takes it explicitly is a surface this brief settles, but the identifier must be a valid Plane prefix.

OOS:
- The find-or-create decision flow (Brief 02)
- Writing any config (Plan B)
- States/labels provisioning (the existing setup path already does this)

#### Brief 02: project-resolver-service

Goal: A resolver turns supplied credentials plus a project name into a concrete project id - finding the project if it exists, creating it if not - and reports which path it took, so the bootstrap can both proceed with the id and tell the operator whether their project was found or freshly created.

Inputs: the client methods from Brief 01; `ITicketingProvisioner.cs` and the contracts around it; `Config.cs` (how `PlaneClientOptions` is built from a loaded config today, which the resolver must NOT depend on); the init dispatch in `Program.cs` at lines 214-229 for how raw credential flags arrive.

Outputs:
- A small resolver (an `IProjectResolver` or an equivalent method) that, given base URL, slug, token, and a project name, returns an existing id or creates and returns a new one.
- A typed outcome distinguishing "found" from "created" alongside the id, returned to the caller.
- Construction of the underlying client from raw credentials rather than from a loaded `.build/config.toml`, since the resolver runs before any config exists on disk.

Acceptance:
- [ ] Given a name that exists, the resolver returns its id and reports found
- [ ] Given a name that does not exist, the resolver creates it and reports created
- [ ] The resolver builds its Plane client from raw credentials with no dependency on `.build/config.toml`
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: `build init` runs before config load by design - it is the command that writes the config - so the resolver cannot reuse the normal config-driven client construction in `Config.cs`; it must build a client straight from the supplied creds. Returning found-versus-created rather than just the id exists because the bootstrap summary needs to say "I pulled the GUID for you" for an existing project versus "I created the project and pulled its GUID" for a new one, which is precisely the distinction the operator cares about at onboarding.

OOS:
- Parsing the credentials file (Brief 03)
- Writing config or provisioning (Brief 04)

## Plan B: One-shot name-based bootstrap

### Goal

After this plan, an operator runs `build init` with workspace credentials and a project name - supplied as flags or piped from a reusable text file - and the command does the entire bootstrap in one shot: resolve or create the Plane project, write `.build/config.toml` with the resolved id (never a typed UUID), provision the local repo and the project's states and labels, make a welcome commit so the first ship works, and print a summary that confirms connectivity and points at the scaffold step if a plan doc is present. An init with no credentials still just writes the template, exactly as today.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 03 | credentials-input-file | Parse a key=value creds file (or stdin) with a project name, feeding init | A | src/ThroughlineBuild.Cli/InitCommand.cs, src/ThroughlineBuild.Cli/Program.cs, src/ThroughlineBuild.Cli/CliArgParser.cs |
| 04 | connected-init-orchestration | Connected init: resolve/create project, write config, provision, summarize | 03 | src/ThroughlineBuild.Cli/InitCommand.cs, src/ThroughlineBuild.Cli/SetupCommand.cs, src/ThroughlineBuild.Cli/Program.cs |
| 05 | welcome-initial-commit | Make a first commit on a fresh repo so the first ship works | 04 | src/ThroughlineBuild.Cli/LocalRepoSetup.cs, src/ThroughlineBuild.Cli/InitCommand.cs |
| 06 | app-doc-scaffold-handoff | Detect a plan doc post-bootstrap and point at the scaffold command | 04 | src/ThroughlineBuild.Cli/InitCommand.cs |

### Briefs - detail

#### Brief 03: credentials-input-file

Goal: An operator can supply the four workspace credentials plus a project name from a simple text file or stdin instead of typing each at a prompt, so a stable creds file can be kept and reused across projects by changing only the project name.

Inputs: `InitCommand.cs` (`Execute`, `PromptForMissingValues`, `ApplyFlags`); the init dispatch and `GetFlagValue` parsing in `Program.cs` at lines 214-229; `CliArgParser.cs`; the `[ticketing]` key names in `Templates/config.toml.template` (`plane_base_url`, `plane_workspace_slug`, `plane_api_token`, `plane_project_id`).

Outputs:
- A parser that reads `key = "value"` lines for `plane_base_url`, `plane_workspace_slug`, `plane_api_token`, and a new `plane_project_name` (with `plane_project_id` accepted to bypass resolution), tolerant of comment lines, blank lines, and quoted or unquoted values.
- A way to feed that file to init: a `--from <file>` flag and reading stdin when it is redirected (init already checks `IsInputRedirected`).
- Precedence: explicit flags override file values; the file supplies only what the operator did not pass as a flag.
- The same key names as the config `[ticketing]` section, so one mental model covers both the file and the config.

Acceptance:
- [ ] A four-field creds file plus `plane_project_name` parses into the init inputs
- [ ] Comment lines, blank lines, and quoted or unquoted values are tolerated
- [ ] Explicit flags take precedence over file values
- [ ] `plane_project_id` present in the file bypasses name resolution

Notes: The operator's stated workflow is to keep one file with the stable workspace creds and change only the project per repo, so the parser must treat the name as the per-project variable and the rest as reusable. Reusing the config's own key names rather than inventing new ones means the file is a familiar shape and could even be a trimmed config file. Flags-over-file precedence matches the rest of the CLI and keeps automation predictable when a file and an explicit flag disagree.

OOS:
- Resolving the name to an id (Plan A)
- The orchestration that consumes these inputs (Brief 04)

#### Brief 04: connected-init-orchestration

Goal: `build init`, when given credentials and a project name, performs the whole bootstrap in one shot - connect to Plane, resolve or create the project, write the config with the resolved id, provision the local repo and the project's states and labels, verify connectivity, and print a summary - while an init with no credentials still just writes the template as it does today.

Inputs: `InitCommand.cs`; the resolver from Brief 02; the parsed inputs from Brief 03; `SetupCommand.cs` and the `ITicketingProvisioner`/`ILocalRepoOps` it uses (reused, not reimplemented); `TestConnectivityAsync` at `PlaneTicketingClient.cs` line 124; the init dispatch in `Program.cs`.

Outputs:
- A connected init mode: when creds and a name are present, init resolves the id, renders the config with that id substituted, runs the existing setup provisioning (git init, managed .gitignore, states, labels) inline, runs `TestConnectivityAsync`, and prints a summary naming the project, its resolved id, and whether it was found or created.
- Unconnected init (no creds) behaves exactly as today: writes the template and refuses to clobber an existing config without `--force`.
- The resolved id is substituted into the written config and is never left as a `REQUIRED_PLANE_PROJECT_ID` placeholder or typed by the operator.

Acceptance:
- [ ] `build init` with creds and an existing project name writes a config carrying the resolved id and reports the project as found
- [ ] `build init` with creds and a new project name creates the project, writes the resolved id, and reports it as created
- [ ] Setup provisioning (git init, .gitignore, states, labels) runs as part of connected init
- [ ] Connectivity is verified and a human-readable summary is printed at the end
- [ ] `build init` with no creds writes the template unchanged and still refuses to clobber without `--force`
- [ ] AOT publish succeeds with no new trim or AOT warnings

Notes: The operator asked for one command, and init is the command they already reach for, so connected init is an additive mode on a known verb rather than a new verb to learn. It reuses `SetupCommand`'s provisioning rather than duplicating it, so the idempotent setup path stays the single source of truth and a later standalone `build setup` re-run remains a no-op. init constructs its own client from the supplied creds because it runs before config load; that ordering is preserved, not changed. The end-of-run summary is the payoff the operator explicitly wanted - confirmation that the connection works and that the GUID was pulled for them.

OOS:
- The welcome commit (Brief 05)
- App-doc scaffolding handoff (Brief 06)
- Changing how non-init commands load config

#### Brief 05: welcome-initial-commit

Goal: After a connected init on a fresh repo, the repository has at least one commit, so the first `build ship` does not hit the no-commit base-ref failure that a brand-new repo otherwise guarantees.

Inputs: `LocalRepoSetup.cs` (`ILocalRepoOps`, `FileSystemLocalRepoOps` - `GitInit`, the gitignore read/write methods); `BaseRefResolver.cs` (the unborn-HEAD failure path hardened under TLB-466); the connected init flow from Brief 04; the managed .gitignore content from `GitignoreManager`.

Outputs:
- An initial-commit step in connected init: after git init and writing .gitignore, stage the tracked bootstrap files (.gitignore at minimum) and create a commit titled like "welcome to throughline build".
- A new stage-and-commit operation on `ILocalRepoOps`, shelled the same way `GitInit` is.
- The commit is made only when the repo has no commits yet; a repo with existing history is left untouched.
- `.build/config.toml`, which holds the token and is gitignored, is never committed.

Acceptance:
- [ ] After connected init on a fresh repo, `git rev-parse HEAD` succeeds
- [ ] The welcome commit contains .gitignore and not `.build/config.toml`
- [ ] A repo that already has commits receives no second bootstrap commit
- [ ] A subsequent `build ship` does not fail with the no-commit base-ref error

Notes: `build setup` creates a repo with zero commits, and ship and worktree resolution both need a base ref, so today a freshly-onboarded repo is dead on arrival for ship until the operator commits something unrelated. The welcome commit closes that gap at the exact moment the repo is created. Committing only .gitignore rather than sweeping in the operator's untracked working files keeps the commit minimal and avoids attributing unrelated files to a bootstrap commit, and the token-bearing config stays ignored. The no-history guard makes the step safe to run against an already-initialized repo.

OOS:
- Committing project source (the operator's job)
- The untracked-file ship hardening (Plan C)

#### Brief 06: app-doc-scaffold-handoff

Goal: When a connected init finishes in a repo that already contains a recognized plan or app doc, the summary points the operator at the scaffold step, so the path from "connected project" to "tickets exist" is visible rather than something the operator has to rediscover.

Inputs: the connected init flow from Brief 04; `ScaffoldCommand.cs` and `OpDocSpecCommand.cs` (the existing scaffold and op-doc entry points); the op-doc location conventions (`docs/op-docs/`, `docs/proposals/`) from `op-doc-spec.md` and op-plan mode.

Outputs:
- A post-bootstrap check that detects a recognized plan or app doc at a known path or glob (under `docs/proposals/` or `docs/op-docs/`) and, when found, prints a clear next-step pointer.
- The summary names the detected doc and shows the exact command to scaffold tickets from it.

Acceptance:
- [ ] When a recognized plan or app doc is present, the init summary names it and shows the scaffold command
- [ ] When none is present, the summary omits the scaffold pointer cleanly
- [ ] No scaffolding side effects occur during init unless explicitly requested

Notes: The operator framed the end state as "you have an app doc, boom it scaffolds it out," but auto-running the ticket-hierarchy scaffold during init is a much larger and less reversible action than a bootstrap should take unattended, so this brief delivers the detection and a visible handoff and leaves the actual scaffolding to the existing explicit command. Detection is bounded to known doc locations to avoid guessing at arbitrary markdown in the repo. Whether init should auto-run scaffold behind an explicit opt-in flag is a deliberate follow-up once the bootstrap path has proven out.

OOS:
- Auto-running the scaffold without an explicit operator request
- Defining a new app-doc format (the existing op-doc and plan-doc specs are reused)

## Plan C: Ship robust to untracked files

### Goal

After this plan, a `build ship` no longer aborts opaquely because of an untracked file in the working tree. The pre-flight accounts for untracked files that would collide with the rebase or fast-forward merge and fails early with an actionable message, and any recurring tool-generated artifact that triggered the abort is added to the managed .gitignore. A genuinely clean tree still ships unchanged.

### Briefs

| # | Slug | Intent | Deps | Files |
|---|------|--------|------|-------|
| 07 | ship-untracked-file-guard | Diagnose the recurring untracked culprit; ignore it or fail pre-flight clearly | - | src/ThroughlineBuild.Phases/ShipPhase.cs, src/ThroughlineBuild.Git/WorkingTreeHygieneGate.cs, src/ThroughlineBuild.Git/ProcessGitClient.cs, src/ThroughlineBuild.Cli/LocalRepoSetup.cs |

### Briefs - detail

#### Brief 07: ship-untracked-file-guard

Goal: A `build ship` stops dying with an opaque git error when the working tree carries an untracked file. The pre-flight either accounts for untracked files that would collide with the rebase or merge, or the recurring artifact that triggers it is ignored, so onboarding-era ships do not fail on a file nobody cares about.

Inputs: `ShipPhase.cs` (the pre-flight at lines 206-243, the rebase at line 437, the fast-forward merge at lines 652-659); `WorkingTreeHygieneGate.cs` (`CheckAsync` and `ShipPreflightAsync`); `ProcessGitClient.cs` (`GetTrackedChangesAsync` at lines 813-850, which filters out `??` untracked lines; `RebaseAsync`; `FastForwardMergeAsync`); `GitignoreManager` in `LocalRepoSetup.cs` for the managed ignore list.

Outputs:
- A diagnosis, recorded in the brief's investigation, of the specific recurring untracked file the operator hit, and a decision: gitignore it if it is a tool-generated build artifact, or harden the pre-flight if it is operator content.
- A ship pre-flight that detects untracked files which would collide with the rebase or merge and fails with an actionable message naming the file and the remedy, before any rebase or merge starts.
- Whichever fix the diagnosis indicates: a new `GitignoreManager` entry for a confirmed artifact, and/or an untracked-file check in the hygiene gate.

Acceptance:
- [ ] The recurring untracked file from the operator's report no longer aborts ship
- [ ] An untracked file that would collide with the merge is reported by pre-flight with a clear message before any rebase or merge starts
- [ ] A genuinely clean tree still ships unchanged
- [ ] If the culprit is a tool artifact, it is added to the managed .gitignore entries

Notes: The pre-flight's dirty check intentionally ignores untracked files - `GetTrackedChangesAsync` drops `??` lines - which is right for "is there uncommitted tracked work" but wrong as a ship gate, because git's rebase and `--ff-only` merge do fail when an untracked file would be overwritten. The fix splits on cause: a tool-generated artifact belongs in .gitignore as the cheap permanent fix, while genuine operator content belongs behind a loud pre-flight error rather than a silent mid-merge abort. Identifying which file recurs is the load-bearing first step, and the investigator settles it against the real tree before choosing the remedy.

OOS:
- Redesigning ship to run from an isolated worktree (a larger change, a separate op)
- Auto-deleting untracked files (destructive, never)

## What done looks like

An operator onboarding a new repo keeps one small credentials file - their Plane base URL, workspace slug, and token - and runs `build init` against it with a project name. If the project exists, init pulls its id; if not, init creates the project and pulls the id - the operator never sees or types a UUID. init writes the config with that id, initializes git, lays down the managed .gitignore, provisions the project's states and labels, and makes a "welcome to throughline build" first commit, then prints a summary that confirms the connection works, names the project and its resolved id, and - if a plan doc is sitting in the repo - points at the scaffold command to turn it into tickets. The first `build ship` from that repo succeeds instead of failing for want of a commit, and a stray untracked file no longer aborts a merge with an opaque git error. Onboarding goes from hand-editing TOML and pasting GUIDs to a single command and a friendly confirmation.
