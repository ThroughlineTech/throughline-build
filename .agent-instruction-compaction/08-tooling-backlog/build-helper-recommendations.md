# Step 8 - Build Helper Recommendations

Purpose: separate instruction redundancy from workflow ceremony that should
become deterministic Build commands.

Source transcript: `backlog-transcript.txt`

## Recommendation 1: `build candidate status --json`

Working title alternatives: `build fingerprint --json`,
`build candidate fingerprint --json`.

Problem:

The conductor repeatedly ran shell blocks to compute base/head state, diff hash,
cached hash, untracked hash, touched paths, candidate SHA, and lease state. The
blocks worked, but they are noisy and easy to mistype.

Recommended command:

```sh
build candidate status --base <ref> [--ticket <ID>] [--json]
```

Output should report:

- base SHA;
- current HEAD SHA;
- tracked diff hash;
- cached diff hash;
- untracked file-list hash;
- touched tracked and untracked paths;
- lease metadata path and ticket ID when present;
- whether HEAD still equals the leased base before conductor commit;
- unsafe-state reason on nonzero exit.

Why it helps:

- replaces repeated manual fingerprint shell;
- makes reviewer-before/after comparisons deterministic;
- shrinks transcript blocks;
- reduces risk of a partial fingerprint.

## Recommendation 2: `build evidence add`

Problem:

The run used valuable ledger comments, but every ticket repeated claim, review,
commit, integrate, gate, and final comment shapes plus readback.

Recommended command:

```sh
build evidence add --ticket <ID> --kind <claim|review|commit|integrate|gate|final> [options] --json
```

Useful options:

- `--tx <transaction-id>`;
- `--sha <sha>`;
- `--base <sha>`;
- `--run-head <sha>`;
- `--fingerprint <json-or-id>`;
- `--gate-summary <text-or-json>`;
- `--verdict PASS|REWORK`;
- `--rework-rounds <n>`;
- `--cleanup-state <text>`.

Why it helps:

- preserves the explicit mutation sequence;
- standardizes audit language;
- makes readback expectations consistent;
- avoids freehand ticket evidence formatting.

## Recommendation 3: `build worker brief`

Problem:

Worker prompts repeat ticket body, acceptance criteria, surface fence, exact
gate, role rules, and safety bans.

Recommended command:

```sh
build worker brief --ticket <ID> --role <implementer|reviewer|strong-reviewer> --worktree <path> --out <path> --json
```

Generated brief should include:

- ticket title, body, comments, and acceptance criteria;
- authorized surface fence;
- exact gate command;
- relevant repo invariants;
- implementer or reviewer contract;
- mutation bans;
- expected response format;
- contract version/hash.

Why it helps:

- conductor can send a short prompt that points to the brief;
- worker instructions become inspectable after the fact;
- duplicate prompt text drops without weakening safety.

## Recommendation 4: Optional `build run summarize`

Problem:

Final reporting is manually assembled from ticket state, commits, gates, leases,
and cleanup checks.

Recommended command:

```sh
build run summarize --tickets <ID...> --branch <branch> --json
```

Output should include:

- per-ticket final state;
- commit SHA;
- review verdict and rework count;
- gate summaries;
- lease teardown status;
- remaining Build leases;
- working tree cleanliness;
- push/deploy/merge status when known.

Why it helps:

- improves final reports;
- gives a deterministic checklist before handoff;
- makes benchmark comparisons easier.

## Priority

1. `build candidate status --json`
2. `build worker brief`
3. `build evidence add`
4. `build run summarize`

The first two attack the biggest transcript and risk sources observed in this
run: fingerprint ceremony and repeated worker prompts.
