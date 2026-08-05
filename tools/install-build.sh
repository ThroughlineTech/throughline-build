#!/usr/bin/env sh
# install-build.sh - install and configure the Throughline Build CLI in a repository.
#
# Two phases, because exactly one step needs a language model and the rest is
# deterministic:
#
#   prepare  Writes .build/config.toml, provisions the Plane project, and emits
#            the repository-interrogation prompt. Then STOPS and hands off.
#   finish   Applies the profile an agent produced, proves the gate runs,
#            installs the SOP host stubs, and verifies the repo is run-ready.
#
# Between the two, a real agent reads the repository and writes the profile
# JSON. This script never guesses the toolchain and never copies one from
# another repo.
#
# Usage:
#   ./install-build.sh prepare [options]
#   ./install-build.sh finish  [--profile FILE] [--skip-gate]
#
# Options for prepare:
#   --plane-url URL     Plane base URL       (default: https://plane.throughlinetech.net)
#   --workspace SLUG    Workspace slug       (default: throughline)
#   --project-id UUID   Plane project UUID   (required unless already configured)
#   --token-env VAR     Env var holding the API token (default: PLANE_API_TOKEN)
#   --force             Overwrite an existing .build/config.toml
#
# Exit codes: 0 ok, 1 step failed, 2 usage/precondition, 3 missing secret.

set -e

PLANE_URL="https://plane.throughlinetech.net"
WORKSPACE="throughline"
PROJECT_ID=""
TOKEN_ENV="PLANE_API_TOKEN"
FORCE=""
PROFILE_FILE=".build/profile.json"
SKIP_GATE=""
PROMPT_FILE=".build/profile-prompt.md"
TICKET_PREFIX=""
RUN_BRANCH="run/backlog"
PROTECTED_BRANCH="main"
ALLOW_PLACEHOLDER=""
CONDUCTOR=".build/conductor.toml"
INVARIANTS_FILE=""

say()  { printf '\n== %s\n' "$1"; }
info() { printf '   %s\n' "$1"; }
die()  { printf '\nERROR: %s\n' "$1" >&2; exit "${2:-1}"; }

# ---------------------------------------------------------------- preconditions

check_binary() {
    say "Checking the build binary"
    command -v build >/dev/null 2>&1 || die "'build' is not on PATH. Build it (./build.sh) and put it on PATH." 2
    BUILD_VERSION=$(build --version 2>&1) || die "'build --version' failed: $BUILD_VERSION" 2
    info "build $BUILD_VERSION"
    info "$(command -v build)"
}

check_repo() {
    say "Checking the repository"
    git rev-parse --is-inside-work-tree >/dev/null 2>&1 || die "not inside a git repository." 2
    info "repository root: $(git rev-parse --show-toplevel)"
    info "branch: $(git rev-parse --abbrev-ref HEAD)"
}

check_token() {
    say "Checking the Plane API token"
    eval "TOKEN_VALUE=\${$TOKEN_ENV:-}"
    if [ -z "$TOKEN_VALUE" ]; then
        printf '\nERROR: %s is not set in this shell.\n' "$TOKEN_ENV" >&2
        printf 'If it lives in a sourced file (e.g. ~/.plane-env via ~/.bashrc), this\n' >&2
        printf 'is a non-interactive shell that skipped it. Run:\n\n' >&2
        printf '    . ~/.plane-env\n\n' >&2
        printf 'then re-run this script.\n' >&2
        exit 3
    fi
    info "$TOKEN_ENV is set (${#TOKEN_VALUE} chars)"
}

# Emits the prompt for the second agent-shaped install step. The binary has
# 'build profile prompt' for the toolchain but no equivalent for conductor
# review invariants, even though both are prose-to-config judgment calls.
write_invariants_prompt() {
    cat > .build/invariants-prompt.md <<'PROMPT'
# Write this repository's review invariants

You are configuring an automated review gate for the repository in your current working
directory. Read the repository: its contributor/agent guide (AGENTS.md, CONTRIBUTING.md,
CLAUDE.md), its architecture documentation, and the structure of its source tree.

Your job is to produce the review invariants that will replace the placeholder one currently
in `.build/conductor.toml`. The installer applies your output; you do not write any file.

A review invariant is a property of this repository that MUST remain true after any change,
stated so a reviewer can decide it by reading a diff. It is not a style preference and not a
restatement of "the build must pass" - the gate already proves that. Good invariants capture
the architectural rules this codebase would silently rot without: layering constraints,
serialization or I/O restrictions, contracts a specific directory must uphold, safety
properties a subsystem must preserve.

Rules:
- Write 2 to 5 invariants. Fewer real ones beat many vague ones.
- Each needs: a short kebab-case `id`, a `statement` a reviewer can judge from a diff, a
  `paths` glob list scoping it to where it applies, and `blocks_done`.
- Set `blocks_done = true` only when a violation genuinely must not ship. Otherwise false.
- Derive them from what the repository ITSELF states. Do not invent generic software advice.
- ASCII only. No em-dashes, no curly quotes.

## Output

Return ONLY the TOML blocks, nothing else. No prose, no markdown fence, no explanation.
Do not edit any file. This response is piped straight into the installer.

Emit 2 to 5 blocks in exactly this shape:

[[conductor.review.invariants]]
id = "some-id"
statement = "A property a reviewer can check against a diff."
paths = ["src/SomeProject/**"]
blocks_done = true
PROMPT
    info "wrote .build/invariants-prompt.md"
}

# ---------------------------------------------------------------------- prepare

do_prepare() {
    check_binary
    check_repo
    check_token

    say "Writing .build/config.toml"
    if [ -f .build/config.toml ] && [ -z "$FORCE" ]; then
        info "already exists; leaving it alone (pass --force to overwrite)"
    else
        [ -n "$PROJECT_ID" ] || die "--project-id is required when creating a new config." 2
        set -- init --no-interactive \
            --plane-url "$PLANE_URL" \
            --workspace "$WORKSPACE" \
            --project-id "$PROJECT_ID" \
            --token-env "$TOKEN_ENV"
        [ -n "$FORCE" ] && set -- "$@" --force
        build "$@" || die "build init failed."
        info "wrote .build/config.toml"
    fi

    say "Provisioning the Plane project (states + labels)"
    if build setup --check >/dev/null 2>&1; then
        info "already provisioned; nothing to create"
    else
        info "gaps found; running build setup (this MUTATES the Plane project)"
        build setup || die "build setup failed."
    fi
    build setup --check || die "build setup --check still reports gaps."

    say "Emitting the repository-interrogation prompt"
    mkdir -p .build
    build profile prompt > "$PROMPT_FILE" || die "build profile prompt failed."
    info "wrote $PROMPT_FILE ($(wc -l < "$PROMPT_FILE") lines)"

    cat <<'HANDOFF'

================================================================================
STOP. The next step needs an agent, not this script.

The gate ([[review.checks]]) is still empty. An agent has to read THIS
repository and decide what its real build and test commands are. Do not copy
them from another repo and do not guess from file extensions.

Pick one:

  A) Non-interactive, one line, from a PLAIN terminal (not inside an agent
     session). Substitute your CLI of choice:

       claude -p "$(cat .build/profile-prompt.md)" > .build/profile.json
       codex exec "$(cat .build/profile-prompt.md)" > .build/profile.json

  B) Open this repo in VS Code, start Claude or Codex, and paste the contents
     of .build/profile-prompt.md. Save the JSON it returns to
     .build/profile.json.

The agent must return ONLY a JSON object - no markdown fence, no prose.

Then come back and run:

    ./install-build.sh finish

================================================================================
HANDOFF
}

# ----------------------------------------------------------------------- finish

do_finish() {
    check_binary
    check_repo
    check_token

    say "Applying the derived profile"
    [ -f "$PROFILE_FILE" ] || die "$PROFILE_FILE not found. Run './install-build.sh prepare' first, then have an agent produce it." 2
    build profile apply "$PROFILE_FILE" --json || die "build profile apply failed."

    say "Configured gating checks"
    grep -A4 '^\[\[review.checks\]\]' .build/config.toml | grep -E '^(name|executable) =' || \
        die "no [[review.checks]] were written; the profile JSON had no review_checks."

    if [ -n "$SKIP_GATE" ]; then
        say "Skipping the gate probe (--skip-gate)"
    else
        say "Proving the gate actually runs"
        info "this runs the repository's real build and test commands - it can take minutes"
        if build gate --require-checks --role gating; then
            info "gate is GREEN on the current tree"
        else
            printf '\n'
            printf 'WARNING: the gate is RED on the current tree.\n'
            printf 'The gate machinery works - it ran the checks and reported failure.\n'
            printf 'But an autonomous run cannot succeed until this tree is green, because\n'
            printf 'every ticket will bounce off a gate that was already failing.\n'
            printf 'Fix the failing checks (or narrow them) before running run-backlog.\n\n'
        fi
    fi

    say "Installing the SOP host stubs"
    build sop install --json >/dev/null || die "build sop install failed."
    info "installed:"
    ls .claude/commands/*.md 2>/dev/null | sed 's/^/     /' || true
    ls .agents/skills/*/SKILL.md 2>/dev/null | sed 's/^/     /' || true

    # 'sop install' emits conductor.toml with PLACEHOLDER values, and 'sop doctor'
    # passes them (it checks shape, not meaning). Both placeholders break a real
    # run, so catch them here rather than three stages into an autonomous chain.
    say "Checking conductor data for placeholders"
    [ -f "$CONDUCTOR" ] || die "$CONDUCTOR not found after sop install."

    if grep -q 'ticket_prefix = "TICKET"' "$CONDUCTOR"; then
        if [ -n "$TICKET_PREFIX" ]; then
            sed -i.bak "s/^ticket_prefix = \"TICKET\"/ticket_prefix = \"$TICKET_PREFIX\"/" "$CONDUCTOR"
            rm -f "$CONDUCTOR.bak"
            info "ticket_prefix set to \"$TICKET_PREFIX\""
        else
            die "conductor.toml still has the placeholder ticket_prefix = \"TICKET\".
   The CLI does not expose your Plane project identifier, so it cannot be derived.
   Re-run with:  --ticket-prefix <PREFIX>     (e.g. --ticket-prefix TLB)" 2
        fi
    else
        info "ticket_prefix: $(grep '^ticket_prefix' "$CONDUCTOR" | cut -d'"' -f2)"
    fi

    # Splice agent-supplied invariants over the placeholder block. The generated
    # file has exactly one [[conductor.review.invariants]] run, terminated by
    # [conductor.review.escalation]; replace that run and touch nothing else.
    if [ -n "$INVARIANTS_FILE" ]; then
        [ -f "$INVARIANTS_FILE" ] || die "invariants file not found: $INVARIANTS_FILE" 2
        grep -q '^\[\[conductor.review.invariants\]\]' "$INVARIANTS_FILE" || \
            die "$INVARIANTS_FILE has no [[conductor.review.invariants]] block. The agent returned prose or a fenced block; re-run it." 2
        grep -q 'Replace this sentence' "$INVARIANTS_FILE" && \
            die "$INVARIANTS_FILE still contains the placeholder sentence." 2
        awk -v nf="$INVARIANTS_FILE" '
            BEGIN { ins = 0; skip = 0 }
            /^\[\[conductor\.review\.invariants\]\]/ {
                if (!ins) { while ((getline l < nf) > 0) print l; ins = 1 }
                skip = 1; next
            }
            /^\[conductor\.review\.escalation\]/ { skip = 0 }
            skip == 0 { print }
        ' "$CONDUCTOR" > "$CONDUCTOR.new" || die "could not splice invariants."
        mv "$CONDUCTOR.new" "$CONDUCTOR"
        info "spliced $(grep -c '^\[\[conductor.review.invariants\]\]' "$CONDUCTOR") invariant(s) into conductor.toml"
    fi

    if grep -q 'Replace this sentence' "$CONDUCTOR"; then
        if [ -n "$ALLOW_PLACEHOLDER" ]; then
            printf '\nWARNING: conductor.toml still carries the placeholder review invariant\n'
            printf '("Replace this sentence...") with blocks_done = true. Review will judge\n'
            printf 'every ticket against a sentence that says nothing. Proceeding because\n'
            printf '--allow-placeholder-invariants was passed.\n\n'
        else
            write_invariants_prompt
            die "conductor.toml still carries the PLACEHOLDER review invariant:

     statement = \"Replace this sentence with a true review invariant for this repository.\"
     blocks_done = true

   Every ticket's review is judged against that sentence, and it blocks Done.
   'build sop doctor' will NOT catch this - it validates shape, not meaning.

   This is the SECOND step that needs an agent. A prompt has been written to
   .build/invariants-prompt.md. From a PLAIN terminal:

     codex exec \"\$(cat .build/invariants-prompt.md)\" > .build/invariants.toml
     claude -p \"\$(cat .build/invariants-prompt.md)\" > .build/invariants.toml

   It writes TOML to stdout and edits nothing, so a read-only agent sandbox is
   fine. Then re-run:  finish --invariants .build/invariants.toml
   To proceed anyway for a smoke test: --allow-placeholder-invariants" 2
        fi
    else
        info "review invariants: $(grep -c '^\[\[conductor.review.invariants\]\]' "$CONDUCTOR") configured"
    fi

    say "Validating conductor data"
    build sop doctor --json >/dev/null || die "build sop doctor failed."
    info "doctor passed"

    say "Checking the run-backlog SOP is servable"
    build sop brief run-backlog --json > .build/sop-brief.json || die "build sop brief run-backlog failed."
    grep -q '"ready": true' .build/sop-brief.json || die "sop brief did not report ready:true."
    info "run-backlog is ready ($(wc -c < .build/sop-brief.json) bytes of SOP text)"
    rm -f .build/sop-brief.json

    say "Verifying ticket access"
    build list --state Backlog >/dev/null 2>&1 || info "(could not list Backlog; check the project id)"
    info "ticketing reachable"

    # The SOP's run preflight REQUIRES a clean tree on a non-protected branch, and
    # it forbids the conductor from stashing, adding, or switching branches to get
    # there. 'sop install' just created untracked files, so an installer that stops
    # here hands over a repository that cannot start a run. Land them on a run
    # branch instead - this is install work, not ticket work.
    say "Preparing the run branch"
    CURRENT_BRANCH=$(git rev-parse --abbrev-ref HEAD)
    if [ "$CURRENT_BRANCH" = "$PROTECTED_BRANCH" ]; then
        if git show-ref --verify --quiet "refs/heads/$RUN_BRANCH"; then
            git checkout "$RUN_BRANCH" >/dev/null 2>&1 || die "could not check out existing branch $RUN_BRANCH."
            info "switched to existing $RUN_BRANCH"
        else
            git checkout -b "$RUN_BRANCH" >/dev/null 2>&1 || die "could not create branch $RUN_BRANCH."
            info "created $RUN_BRANCH off $PROTECTED_BRANCH"
        fi
    else
        info "already on non-protected branch $CURRENT_BRANCH"
    fi

    say "Committing the SOP host stubs"
    for path in .agents .claude/commands; do
        [ -e "$path" ] && git add "$path"
    done
    if git diff --cached --quiet; then
        info "nothing to commit; stubs already tracked"
    else
        git commit -q -m "build: install run-backlog SOP host stubs" || die "could not commit the SOP host stubs."
        info "committed: $(git rev-parse --short HEAD)"
    fi

    say "Asserting the SOP run preflight passes"
    PORCELAIN=$(git status --porcelain)
    if [ -n "$PORCELAIN" ]; then
        printf '\nERROR: tree is still dirty; run-backlog will hard-stop at preflight.\n' >&2
        printf '%s\n' "$PORCELAIN" >&2
        printf '\nCommit, ignore, or remove these before starting a run.\n' >&2
        exit 1
    fi
    info "git status --porcelain: empty"

    FINAL_BRANCH=$(git rev-parse --abbrev-ref HEAD)
    [ "$FINAL_BRANCH" != "$PROTECTED_BRANCH" ] || die "still on protected branch $PROTECTED_BRANCH."
    info "branch: $FINAL_BRANCH (not protected)"

    git rev-parse -q --verify MERGE_HEAD >/dev/null 2>&1 && die "an interrupted merge is in progress."
    [ ! -d .git/rebase-merge ] && [ ! -d .git/rebase-apply ] || die "an interrupted rebase is in progress."
    info "no interrupted merge/rebase"

    build worktree list --json >/dev/null 2>&1 && info "worktree leases: queryable" || info "(worktree list unavailable)"

    cat <<DONE

================================================================================
READY. The SOP run preflight passes:

    branch                    $FINAL_BRANCH (not $PROTECTED_BRANCH)
    git status --porcelain    empty
    merge/rebase in progress  none

Open this repository in VS Code, start Claude or Codex, and run:

    /run-backlog <ticket-id>

If the gate reported RED above, fix that first - an autonomous run cannot
close a ticket through a gate that was already failing before it started.
================================================================================
DONE
}

# -------------------------------------------------------------------- arg parse

[ $# -ge 1 ] || die "usage: install-build.sh prepare|finish [options]" 2
COMMAND="$1"; shift

while [ $# -gt 0 ]; do
    case "$1" in
        --plane-url)  PLANE_URL="$2"; shift 2 ;;
        --workspace)  WORKSPACE="$2"; shift 2 ;;
        --project-id) PROJECT_ID="$2"; shift 2 ;;
        --token-env)  TOKEN_ENV="$2"; shift 2 ;;
        --profile)    PROFILE_FILE="$2"; shift 2 ;;
        --ticket-prefix) TICKET_PREFIX="$2"; shift 2 ;;
        --invariants) INVARIANTS_FILE="$2"; shift 2 ;;
        --run-branch)    RUN_BRANCH="$2"; shift 2 ;;
        --protected-branch) PROTECTED_BRANCH="$2"; shift 2 ;;
        --allow-placeholder-invariants) ALLOW_PLACEHOLDER=1; shift ;;
        --force)      FORCE=1; shift ;;
        --skip-gate)  SKIP_GATE=1; shift ;;
        *) die "unknown option: $1" 2 ;;
    esac
done

case "$COMMAND" in
    prepare) do_prepare ;;
    finish)  do_finish ;;
    *) die "unknown command '$COMMAND' (expected 'prepare' or 'finish')" 2 ;;
esac
