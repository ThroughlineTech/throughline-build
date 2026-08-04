# New repository setup: Plane and run-backlog

Use this runbook to prepare a repository so you can open Claude Code in VS Code,
type `/run-backlog`, and have it drive a real ticket to Done.

Run every command from the repository root. The current binary is the authority;
use `build <command> --help` if its command contract differs from this document.

## Who runs the work

`run-backlog` does NOT use `build chain`, `plan`, `implement`, or `review`. The
SOP names those the "deliberately-rejected alternative" and tells you not to
adopt them. Your interactive Claude session IS the conductor: it holds context
across the whole run, spawns its own subagents as implementers and reviewers,
and calls only deterministic `build` verbs.

| Concern | Owner |
| --- | --- |
| workspace lifecycle | `build worktree lease \| list \| teardown` |
| which tickets may run together | `build waves --input` |
| what "green" means | `build gate --ticket <ID> --require-checks --json` |
| every commit, branch, and ticket mutation | the conductor, in the primary worktree |
| implement / review judgment | the conductor's subagents, read-implement-gate only |

Two consequences matter for setup:

- **No worker CLI is spawned**, so nothing here trips the nested-session guard.
  You can complete this entire runbook from inside a Claude session.
- **`[[review.checks]]` is load-bearing.** A run with no configured checks does
  not degrade to "unverified but proceeding"; `checksConfigured: false` aborts
  the whole run. An empty gate is not a partial setup, it is a broken one.

## Understand the files

Two TOML files have different owners and must not be combined:

| File | Contents | Git policy |
| --- | --- | --- |
| `.build/config.toml` | Plane connection, token environment-variable name, workers, events, and executable review checks | Machine-local and ignored |
| `.build/conductor.toml` | SOP version floor, ticket and branch prefixes, source roots, architecture map, review invariants, and escalation paths | Tracked |

The SOP installer also emits two tracked host stubs and one installer cache:

- `.claude/commands/run-backlog.md` for Claude;
- `.agents/skills/run-backlog/SKILL.md` for Codex; and
- `.build/sop-manifest.json`, a cache of what a prior install wrote.

The embedded catalog, not the manifest, is the authority for emitted stub
content. Never put a Plane token in `conductor.toml`, a host stub, or a
temporary TOML file that Git can see. Note that `.gitignore` covers `.build/`,
so a hand-made backup directory such as `.build-bak/` is NOT ignored and will
expose a literal token to `git add -A`.

## 1. Check prerequisites

Install `build`, Git, and Claude Code, then confirm the same shell finds them:

```sh
build --version
git --version
claude --version
```

Install and authenticate Codex too if you want the Codex stub to be usable.
Have these Plane values ready: API base URL, workspace slug, existing project
UUID (or a project name to find or create), and a personal API token.

## 2. Put the Plane token in the environment

In Git Bash, read the token without echoing it or placing its value in shell
history:

```sh
read -rsp "Plane API token: " PLANE_API_TOKEN
export PLANE_API_TOKEN
printf '\n'
```

The important distinction is:

```text
shell environment:  PLANE_API_TOKEN=<the secret value>
config.toml:         plane_api_token_env = "PLANE_API_TOKEN"
```

`--token-env` takes the environment variable's NAME. Do not pass
`--token-env "$PLANE_API_TOKEN"`; that expands the secret and incorrectly makes
the token itself the environment-variable name.

VS Code inherits the environment of the process that launched it. If you export
the token in a terminal that VS Code did not spawn from, the Claude session will
not see it. Export it, then launch VS Code from that same shell, or set the
variable persistently for your user account.

## 3. Create `.build/config.toml` and connect Plane

For an existing Plane project:

```sh
build init --no-interactive \
  --plane-url "PLANE_API_URL" \
  --workspace "WORKSPACE_SLUG" \
  --project-id "PROJECT_UUID" \
  --token-env PLANE_API_TOKEN
build setup
```

An explicit `--project-id` uses the offline initialization path: it writes the
complete configuration but does not contact or provision Plane, so `build setup`
must follow it.

To find or create a project by name and provision in one connected command, omit
`--project-id` and pass `--project-name` instead. Connected initialization
resolves or creates the project, writes its UUID, provisions the required Plane
states and labels, initializes Git when needed, and verifies connectivity.

Do not use `build init --print-template` as the installation step. It prints a
`config.toml` template to stdout and writes no configuration; do not redirect it
to `conductor.toml`.

If `.build/config.toml` already exists, stop and inspect it. `build init` refuses
to overwrite without `--force`. Do not use `--force` on an established repository
until its `[[review.checks]]` have been preserved; the generated template
intentionally ships with every check commented out.

Verify the connection with a real ticket read:

```sh
rg -n "REQUIRED_" .build/config.toml   # must produce no output
build setup --check
build list --json
```

`build list` is the connectivity check that matters. A passing `build sop doctor`
does NOT prove Plane connectivity, because SOP commands never load ticketing
credentials.

## 4. Fill in the gate

This is the step that actually decides whether a run works, and it is the step
worth spending an agent on rather than hand-authoring TOML.

The generated config has every `[[review.checks]]` block commented out. You need
at least one real gating check, and it must be capable of FAILING.

### Let an agent derive it

The binary already contains the canonical derivation rules, embedded at
[src/ThroughlineBuild.Scaffold/Templates/derive-profile-prompt.md](../src/ThroughlineBuild.Scaffold/Templates/derive-profile-prompt.md).
`build scaffold <op-doc>` applies them automatically, but that path requires an
op-doc and creates a ticket tree. When you are configuring an existing repository
by hand, paste the rules into your Claude session and point them at the repo:

> Read this repository and derive its toolchain gate. Follow the rules in
> `src/.../derive-profile-prompt.md` (read the file). Emit the `PROJECT_PROFILE`
> JSON block, then write the resulting `[[review.checks]]` and
> `[[ship.regression_checks]]` into `.build/config.toml`, leaving every other
> section and comment untouched.

Do not shorten those rules to "figure out my build and test commands". The
derivation rules exist because the obvious answer produces a gate that cannot
fail. They require, at minimum:

- **role on every check.** `gating` hard-fails the run; `advisory` is recorded
  but never blocks. Build, test, typecheck, and compile are gating. Lint, format,
  and style are advisory, because a false-gating burns a cold rework loop on
  something auto-fixable.
- **non-vacuity.** A command that inspects nothing always passes. A `tsc --noEmit`
  against a project-references root with `files: []` follows no references and
  checks zero files.
- **a canary per gating check.** The smallest deliberately-broken file the check
  MUST reject, carried as `canary = [{ path, content }]`. This is the mechanism
  that proves the gate can fail. For a test check, the canary is a deliberately
  failing test, which guards against a runner that collects zero tests and
  reports green.
- **required_paths on every gating and setup check**, so a check is not run
  against a tree that cannot support it.
- **a hermetic test command.** The runner must not collect `.worktrees/` or
  `.build/`, or a root test run goes red from the engine's own scratch copies.
- **no user-global tool caches.** Every check runs in a freshly created throwaway
  worktree, and the same check runs against different code in different
  worktrees. A tool with a global path-keyed cache can replay a stale verdict
  into a ship baseline. Pass the cache-disabling flag when one exists.

### Watch the freshly-created-worktree trap

Checks run in throwaway worktrees, not in your working tree. Any check that
assumes a prior step has run in that directory will fail on first use. In
particular, `--no-restore` and `--no-build` style flags are only safe when an
earlier `setup` or gating check in the same list has produced their inputs.

### Verify it

```sh
build gate --require-checks --json
```

`--require-checks` makes an empty list a failure instead of a silent pass. A zero
check gate is not a successful setup.

## 5. Install run-backlog

```sh
build sop install --sop run-backlog --json
```

Omitting `--host` installs both known host adapters. Expected result:

```text
.claude/commands/run-backlog.md
.agents/skills/run-backlog/SKILL.md
```

Install is idempotent, and it creates `.build/conductor.toml` only when that file
is missing. It never overwrites an existing conductor file.

The Claude stub is deliberately five lines long. It does not contain the SOP; it
tells the session to run `build sop brief run-backlog --json` and follow the text
that command returns. The SOP prose therefore always comes from the installed
binary and can never go stale in a checked-in file.

## 6. Replace the conductor scaffold with repository facts

`build sop brief` runs doctor first and withholds `data.sopText` entirely when
doctor fails, so an unedited scaffold means `/run-backlog` dead-ends immediately.
Open `.build/conductor.toml` and replace every generic value. At minimum verify:

- `min_build_version` is no newer than the installed binary;
- `branch_prefix` matches the repository's branch convention;
- `ticket_prefix` matches the Plane project identifier;
- `source_roots` covers the code and current architecture documentation;
- `architecture_map` names a tracked, current file;
- every review invariant is true and repository-specific;
- escalation paths identify the repository's high-risk surfaces;
- `platform` describes the target platform; and
- `contract_authority` names the real shared-contract authority.

Doctor validates the SHAPE of `[[conductor.review.invariants]]`, not the truth of
each statement, so it will accept a well-formed lie. Delete the scaffold sentence
that tells you to replace it with a true invariant. `conductor.toml` must contain
no secrets and should be committed alongside the host stubs.

Also review `[waves]` in `config.toml`. `cap` defaults to 2, which means two
tickets can run concurrently. Set `cap = 1` for a first dogfood run so there is
exactly one transaction in flight and any failure is unambiguous.

## 7. Verify the completed installation

Run all of these; they prove different things:

```sh
build setup --check                          # local + Plane setup
build list --json                            # a real ticket read
build gate --require-checks --json           # at least one real check, and it passes
build sop status --sop run-backlog --json    # no stub drift
build sop doctor --json                      # no conductor, stub, or review-check findings
build sop brief run-backlog --json           # returns data.sopText
```

The last one is the true readiness check: it is exactly what `/run-backlog` runs
first, and it fails closed. If it returns `sopText`, the session can start.

Common doctor findings and what each means:

| Finding | Cause |
| --- | --- |
| `review.checks.empty` | Step 4 was skipped; every check is still commented out |
| `review.checks.command.missing` | A check names an executable that cannot be resolved |
| `review.checks.role.invalid` | `role` is not `gating`, `advisory`, or `setup` |
| `conductor.file.missing` | `build sop install` has not run |
| `conductor.review.invariants.empty` | Step 6 was skipped |
| `sop.stub.modified` / `sop.stub.drift` | A stub was hand-edited, or was written by an older binary |
| `sop.stub.missing` | A stub that was installed is gone; with no manifest, doctor uses the Git index to know it was installed |
| `sop.stub.scope_unavailable` | A stub is absent, there is no manifest, and Git could not be asked whether it was ever installed |

## 8. Check Git ownership before committing

```sh
git status --short
git check-ignore -v .build/config.toml
```

`.build/config.toml` must be ignored. Review and commit the tracked contract and
stubs:

```text
.build/conductor.toml
.claude/commands/run-backlog.md
.agents/skills/run-backlog/SKILL.md
```

Treat `.build/sop-manifest.json` as installer history, not as authority for
modified stubs. Before committing, confirm no temporary configuration file holds
a literal Plane token and no broad `.build/` ignore rule hides the tracked
`conductor.toml`.

## 9. Run a ticket

Start a FRESH Claude session from the repository root, so the host discovers the
newly installed command, then:

```text
/run-backlog
```

Preflight requires a clean primary worktree on a non-protected branch, with no
interrupted merge or rebase and no outstanding leases. A dirty tree stops the
run by design: integration commits and the merged gate both run in the primary
tree, so absorbing unrelated work would put unreviewed changes inside a ticket's
proof. Commit or stash your own work first.

For a first dogfood run, pick one small ticket with a narrow, predictable file
surface, and set `[waves].cap = 1`.

What you should see: the conductor leases a worktree, proves the baseline is
green BEFORE implementing, transitions the ticket to In Progress, dispatches a
subagent that may read, implement, and gate but may not commit, reviews, then
commits from the primary tree with `git -C <lease>`, integrates, re-gates the
merged tree, and only then transitions to Done. A ticket reaching In Review means
a candidate commit exists but is not yet proven integrated.

Escalation is a successful outcome. Three failed rework rounds, a baseline red,
or a rebase conflict at integration all stop with the worktree, branch, diff, and
finding history preserved for you to inspect.

## Troubleshooting

### `/run-backlog` reports the SOP could not be loaded

`build sop brief` failed, so the session correctly refused to improvise. Run
`build sop doctor --json` directly and fix the findings. The stub is instructed
never to fall back to cached prose.

### `missing required TOML section [ticketing]`

The nearest `.build/config.toml` is partial. Ticket commands require
`[ticketing]`, `[workers]`, and `[events]`. Re-run initialization for a genuinely
new repository, or merge the missing sections without replacing existing review
checks.

### `required environment variable '<token text>' is not set`

`plane_api_token_env` holds the secret value instead of the variable name. Set it
to `plane_api_token_env = "PLANE_API_TOKEN"` and export the token under that name
in the shell that launched VS Code.

### `sop doctor` passes but `build list` fails

Expected by design. Doctor validates conductor data, emitted stubs, and the shape
of `[[review.checks]]`; it never loads or tests Plane configuration. Use
`build setup --check` and `build list --json` for the ticketing path.

### The gate passes but nothing was really checked

Run the canary probe. A gating check with no canary has never been proven able to
fail, and a check that inspects an empty aggregate root passes forever. Re-derive
the profile with the full rules from step 4.

### Only one host stub exists

Re-run the unfiltered `build sop install --sop run-backlog --json`, then confirm
with `build sop status --sop run-backlog --json`.
