"""Minimize the vendored analysis corpus for public distribution.

The analysis scripts need event identity, phase, ticket, model, usage, timing,
verdict class, and side-effect action. They do not need operator paths, backend
identifiers, commit SHAs, review prose, or raw check output.

Run with --write to rewrite every JSONL file under ../data in place. The
transformation is deterministic and idempotent for an already-sanitized corpus.
"""

import argparse
import json
import os
import re
from pathlib import Path


HERE = Path(__file__).resolve().parent
DATA = HERE.parent / "data"

SESSION_RE = re.compile(r"^session-\d{4}$")
BUILD_RE = re.compile(r"^build-\d{2}$")

ROOT_KEYS = (
    "SessionId",
    "Timestamp",
    "Kind",
    "TicketId",
    "Phase",
)

DATA_KEYS_BY_KIND = {
    0: ("from", "to"),
    1: (
        "model",
        "vendor",
        "wall_clock_ms",
        "input_tokens",
        "output_tokens",
        "cache_read_tokens",
        "cache_create_tokens",
        "cached_input_tokens",
        "cost_usd",
    ),
    2: ("worker", "role"),
    3: ("status", "kind", "checks_failed_count", "checks_failed"),
    5: ("action", "count", "names"),
    6: ("starting_at_phase", "initial_state"),
    7: ("outcome", "phases_run", "rework_rounds", "total_duration_ms"),
    8: ("outcome", "count"),
    13: ("outcome", "count"),
}

ARM_A_KEYS = (
    "label",
    "command",
    "session_id",
    "ts",
    "model",
    "input",
    "output",
    "cache_read",
    "cache_create",
    "subagent_input",
    "subagent_output",
    "subagent_cache_read",
    "subagent_cache_create",
)


class StableLabels:
    def __init__(self) -> None:
        self.sessions: dict[str, str] = {}
        self.builds: dict[str, str] = {}
        self.session_count = 0
        self.build_count = 0

    def session(self, value: object) -> object:
        if not isinstance(value, str) or not value:
            return value
        if SESSION_RE.fullmatch(value):
            self.session_count = max(self.session_count, int(value.removeprefix("session-")))
            return value
        if value not in self.sessions:
            self.session_count += 1
            self.sessions[value] = f"session-{self.session_count:04d}"
        return self.sessions[value]

    def build(self, value: object) -> object:
        if not isinstance(value, str) or not value:
            return value
        if BUILD_RE.fullmatch(value):
            self.build_count = max(self.build_count, int(value.removeprefix("build-")))
            return value
        if value not in self.builds:
            self.build_count += 1
            self.builds[value] = f"build-{self.build_count:02d}"
        return self.builds[value]


def clean_event(event: dict[str, object], labels: StableLabels) -> dict[str, object]:
    cleaned = {key: event[key] for key in ROOT_KEYS if key in event}
    if "SessionId" in cleaned:
        cleaned["SessionId"] = labels.session(cleaned["SessionId"])

    kind = event.get("Kind")
    data = event.get("Data")
    if isinstance(data, dict):
        allowed = DATA_KEYS_BY_KIND.get(kind, ())
        cleaned["Data"] = {key: data[key] for key in allowed if key in data}

    build_version = event.get("build_version")
    if build_version:
        cleaned["build_version"] = labels.build(build_version)
    return cleaned


def clean_arm_a(row: dict[str, object], labels: StableLabels) -> dict[str, object]:
    cleaned = {key: row[key] for key in ARM_A_KEYS if key in row}
    if "session_id" in cleaned:
        cleaned["session_id"] = labels.session(cleaned["session_id"])
    return cleaned


def rewrite(path: Path, labels: StableLabels, write: bool) -> tuple[int, int, int]:
    output: list[str] = []
    changed = 0
    malformed = 0
    total = 0
    arm_a = path == DATA / "arm-a" / "runs.jsonl"

    for line_number, raw_line in enumerate(
        path.read_text(encoding="utf-8").splitlines(), start=1
    ):
        if not raw_line.strip():
            continue
        total += 1
        try:
            row = json.loads(raw_line)
        except json.JSONDecodeError:
            malformed += 1
            print(f"dropping malformed row: {path.relative_to(DATA)}:{line_number}")
            continue
        cleaned = clean_arm_a(row, labels) if arm_a else clean_event(row, labels)
        rendered = json.dumps(cleaned, separators=(",", ":"), ensure_ascii=True)
        output.append(rendered)
        if rendered != raw_line:
            changed += 1

    if write:
        path.write_text("\n".join(output) + "\n", encoding="utf-8", newline="\n")
    return total, changed, malformed


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--write",
        action="store_true",
        help="rewrite the corpus; without this flag only report pending changes",
    )
    args = parser.parse_args()

    labels = StableLabels()
    total = 0
    changed = 0
    malformed = 0
    paths = sorted(DATA.rglob("*.jsonl"), key=lambda path: path.as_posix())
    for path in paths:
        file_total, file_changed, file_malformed = rewrite(path, labels, args.write)
        total += file_total
        changed += file_changed
        malformed += file_malformed

    action = "rewrote" if args.write else "would rewrite"
    print(
        f"{action} {changed} of {total} rows across {len(paths)} files; "
        f"{labels.session_count} session labels; {labels.build_count} build labels; "
        f"{malformed} malformed rows dropped"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
