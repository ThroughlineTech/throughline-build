# New repository setup: Plane and run-backlog

Use this runbook to prepare a repository so you can open Claude Code in VS Code,
type `/run-backlog`, and have it drive a real ticket to Done.

Run every command from the repository root. The current binary is the authority;
use `build <command> --help` if its command contract differs from this document.

## Fixed installation sequence

`build install` is the stack-agnostic, resumable path. It runs no worker and
does not execute the target repository's build or test commands:

```sh
build install
# Give the emitted prompt to an agent; save its JSON as .build/profile.json.
build install --profile .build/profile.json
# Give the emitted prompt to an agent; save its TOML as .build/invariants.toml.
build install --invariants .build/invariants.toml
```

The first two invocations stop at explicit handoffs. The third reports READY
only after doctor passes, at least one blocking review check exists, generated
placeholders are gone, the Plane token actually resolves (not just that config
parses - see step 3 below on why an interactive shell's resolution is not
enough), and the exact installer-owned readiness paths are committed on a
non-protected run branch: a deterministic `.gitignore` when Setup created or
changed it, plus catalog-emitted Claude/Codex stubs. The porcelain status must
be empty, no merge or rebase may be active, and the worktree lease list must be
queryable. Rerunning a stage is safe: matching profile/invariant data, the run
branch, and the readiness commit are not duplicated. Use `<command> --json` for
one machine-readable result envelope; progress remains on stderr.

The detailed steps below remain useful for diagnosis and for operating the
individual commands directly.

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
| `.build/config.toml` | Plane connection, the token environment-variable name and/or token-file path, workers, events, and executable review checks | Machine-local and ignored |
| `.build/conductor.toml` | SOP version floor, ticket and branch prefixes, source roots, architecture map, review invariants, and escalation paths | Machine-local and ignored; `build install` recreates it per clone |
| `secrets/plane-api-token` (or wherever `plane_api_token_file` points) | The Plane API token, trimmed, and nothing else | Machine-local and ignored (`secrets/` is a reserved gitignore entry) |

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

**This is only a starting point, not the final state.** An environment variable
set this way is only visible to processes descended from THIS shell. Bash
sources `~/.bashrc` only for interactive shells; `zsh -c` does not read
`~/.zshrc`; `bash -c` does not read `~/.bash_profile`; and an agent harness or
an editor launched without a login shell inherits none of them. `/run-backlog`
itself calls only deterministic `build` verbs, and every one of those is a
non-interactive invocation from whatever process spawned the session - so once
you finish step 3 below, persist the token to a file (step 3 shows how) rather
than relying on this export surviving into every future shell that runs
`build`.

## 3. Create `.build/config.toml`, connect Plane, and persist the token to a file

For an existing Plane project:

```sh
build init --no-interactive \
  --plane-url "PLANE_API_URL" \
  --workspace "WORKSPACE_SLUG" \
  --project-id "PROJECT_UUID" \
  --token-env PLANE_API_TOKEN
build setup
build setup --write-token-file secrets/plane-api-token
```

An explicit `--project-id` uses the offline initialization path: it writes the
complete configuration but does not contact or provision Plane, so `build setup`
must follow it.

`build setup --write-token-file` writes the token this run already resolved (the
environment variable from step 2, here) into `secrets/plane-api-token` - a path
already reserved in the generated `.gitignore` - and sets
`plane_api_token_file = "secrets/plane-api-token"` in `.build/config.toml`. It
never prints the token to stdout, stderr, or any log; the config mutation only
ever carries the path. From then on, resolution order is `plane_api_token`, then
the `plane_api_token_env` variable (unchanged, so CI that already exports the
token keeps working exactly as before), then `plane_api_token_file`. Unlike the
environment variable, the file's contents do not depend on which shell (or
whether it was interactive) launched `build`, so `/run-backlog`'s own
non-interactive `build` calls resolve the token the same way your terminal did
in step 2 - and so does any other agent harness, cron job, or CI runner that
inherits this clone. On macOS or Linux, keep the file readable only by you
(`chmod 600 secrets/plane-api-token`); `build` warns, but does not fail, if it
finds looser permissions.

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

This is the step that actually decides whether a run works. Have the active
agent inspect the repository's real build and test entry points using the
binary-hosted rules, then apply the resulting JSON deterministically:

```sh
build profile prompt > profile-rules.md
# Have the active agent inspect the repository and write only PROJECT_PROFILE JSON to profile.json.
build profile apply profile.json --json
```

`profile prompt` starts no worker. `profile apply` writes only the
profile-managed values in `.build/config.toml`: `[project]`,
`[[review.checks]]`, and `[[ship.regression_checks]]`. It preserves every other
section and comment. `profile apply -` reads the JSON from standard input. These
commands need neither ticketing credentials nor worker configuration.

Apply refuses to overwrite customized checks and exits nonzero without changing
the file. Use `--force` only when you intend to replace them. Applying a profile
that already matches also exits nonzero instead of reporting a successful no-op.

### Optional canary proof

Canary proving is an explicit opt-in step. It creates a temporary worktree, runs
the proposed setup and gating checks there, and writes each gating check's
deliberately broken canary. A check that remains green is rejected as vacuous:

```sh
build profile verify-canaries profile.json --json
```

This verb never changes `.build/config.toml` and is not run by profile prompt,
profile apply, scaffold, or SOP installation. The prompt's rules require
role-tagged checks, required paths, hermetic test discovery, no user-global cache
poisoning, and one canary per gating check.

### Offline fallback

If no worker or agent is available, hand-author the profile-managed TOML values.
This is the fallback, not the normal installation path. Preserve the same
non-vacuity and canary requirements, then verify the resulting gate:

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

**Use `install`, not `upgrade`, when a path is absent.** The two verbs are not
interchangeable and the distinction bites on fresh clones:

| Verb | What it does | When |
| --- | --- | --- |
| `sop install` | Writes emitted paths that are missing; scaffolds `conductor.toml` when absent; never overwrites a locally modified stub | First install, every clone missing its local conductor, or any clone missing stubs |
| `sop upgrade` | Rewrites emitted files that ALREADY EXIST and still match a trusted previous catalog hash. It does NOT create missing files | After replacing the binary with a newer one |

Running `sop upgrade` on a repository whose stubs were never installed reports
every path as `missing` with `ok: false`. That is correct behavior, not a
failure: nothing existed to reconcile. The message says so directly, and the fix
is `sop install`.

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
no secrets. It is machine-local and ignored; recreate and verify it in every
clone rather than committing it with the host stubs.

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
git check-ignore -v .build/config.toml .build/conductor.toml .build/sop-manifest.json
```

Both `.build/config.toml` and `.build/conductor.toml` must be ignored. The
installer commits only its exact readiness paths:

```text
.gitignore                         # only when Setup created or changed it deterministically
.claude/commands/run-backlog.md
.agents/skills/run-backlog/SKILL.md
```

Treat `.build/sop-manifest.json` as installer history, not as authority for
modified stubs. Before committing, confirm no temporary configuration file holds
a literal Plane token outside the ignored `.build/` directory.

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

## 10. Set up a second machine

What a clone gives you depends on whether someone completed this runbook and
committed the host stubs. The machine-local conductor file never travels. Check
before assuming:

```sh
git log --oneline -- .claude/commands .agents/skills   # empty means never committed
ls .build/conductor.toml
```

**If the stubs are absent**, this machine is doing a first install, not a
migration. Do the whole runbook from step 2. `build sop upgrade` is the wrong
verb here and will report every path missing.

**If the stubs are present in the clone**, recreate the ignored configuration
and conductor data for this machine:

```sh
build --version                        # must be >= conductor.min_build_version
export PLANE_API_TOKEN=...
build init --no-interactive --plane-url ... --workspace ... --project-id ... --token-env PLANE_API_TOKEN
build setup
build setup --write-token-file secrets/plane-api-token
# re-create [[review.checks]] -- see below
build sop install --json               # creates the missing local conductor without replacing stubs
# replace the conductor scaffold with this repository's verified facts
build sop doctor --json
build sop brief run-backlog --json     # returns sopText => ready
```

`secrets/` is gitignored like `.build/`, so this machine's token file does not
travel with the clone either; run `build setup --write-token-file` again here,
same as the first machine.

The gate does not travel. `.build/config.toml` is gitignored, and it holds
`[[review.checks]]`, `[[ship.regression_checks]]`, `[waves]`, and `[worktree]`
alongside the Plane connection. Those are repository facts, not machine facts,
but they live in the one file a clone never brings. Every new machine therefore
re-creates the gate, and nothing structurally guarantees it matches the first
machine's.

Until that is fixed, copy `[[review.checks]]` across by hand rather than
re-deriving it, and diff the two files. Re-deriving invites two machines to
enforce different gates while both report green.

The SOP prose itself has no such problem: it lives in the binary, so the only
thing governing fidelity is the binary version. `min_build_version` in the local
`conductor.toml` is a floor, not a pin. An older binary is caught; a newer one
silently supplies different SOP prose to the same repository.

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

### `plane_api_token not set in config, environment variable '...' is not set, and ...`

The token resolves in your own terminal but not in the shell that actually ran
`build` - the classic case is an agent harness or a non-interactive shell that
never sourced `~/.bashrc`/`~/.zshrc`/`~/.bash_profile`, so an export placed only
there is invisible to it. This is the exact failure step 3 above exists to
prevent. The fix is the same regardless of which non-interactive process hit it:

```sh
build setup --write-token-file secrets/plane-api-token
```

Run it from a shell where the token DOES currently resolve (your own terminal,
right after step 2). It persists that same token to a file and points
`plane_api_token_file` at it, and that file resolves identically no matter which
shell or agent harness invokes `build` afterward. The error message itself names
every source it tried - the config key, the environment variable name, and the
token file path - so if it did not mention `plane_api_token_file` at all, config
does not have one set yet.

### `sop doctor` passes but `build list` fails

Expected by design. Doctor validates conductor data, emitted stubs, and the shape
of `[[review.checks]]`; it never loads or tests Plane configuration. Use
`build setup --check` and `build list --json` for the ticketing path.

### The gate passes but nothing was really checked

Run `build profile verify-canaries profile.json --json`. A gating check with no
canary has never been proven able to fail, and a check that inspects an empty
aggregate root passes forever. Regenerate the JSON with the full rules from
step 4, verify it, then apply it.

### Only one host stub exists

Re-run the unfiltered `build sop install --sop run-backlog --json`, then confirm
with `build sop status --sop run-backlog --json`.
