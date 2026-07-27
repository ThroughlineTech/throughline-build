"""Fail when tracked publication artifacts contain known private exhaust."""

import re
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SELF = Path(__file__).resolve()
FIXTURES = ROOT / "tests" / "ThroughlineBuild.Workers.ClaudeCode.Tests" / "Fixtures"
ANALYSIS_DATA = ROOT / "docs" / "analysis" / "data"
DERIVED_ROWS = ROOT / "docs" / "analysis" / "scripts" / "lf_rows.json"

GLOBAL_TERMS = (
    "fu" + "bar",
    "project-" + "lattice",
    "re" + "jog",
    "sst" + "14",
)
FIXTURE_TERMS = (
    "G" + "mail",
    "In" + "deed",
    "bypass" + "Permissions",
    '"sig' + 'nature"',
)
ANALYSIS_TERMS = (
    '"project' + '_id"',
    '"workspace' + '_slug"',
    '"ratio' + 'nale"',
    "C:" + "\\\\Users\\\\",
    "C:" + "/Users/",
)
RAW_BUILD = re.compile(r'"build_version":"(?:0\.1\.0\+)?[0-9a-f]{7,40}"')


def tracked_paths() -> list[Path]:
    result = subprocess.run(
        ["git", "ls-files", "-z"],
        cwd=ROOT,
        check=True,
        capture_output=True,
    )
    return [
        ROOT / entry.decode("utf-8")
        for entry in result.stdout.split(b"\0")
        if entry
    ]


def is_under(path: Path, directory: Path) -> bool:
    try:
        path.relative_to(directory)
        return True
    except ValueError:
        return False


def main() -> int:
    findings: list[str] = []
    for path in tracked_paths():
        if path == SELF or not path.is_file():
            continue
        try:
            text = path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            continue

        relative = path.relative_to(ROOT).as_posix()
        for term in GLOBAL_TERMS:
            if term.lower() in text.lower():
                findings.append(f"{relative}: contains private term {term!r}")

        if is_under(path, FIXTURES):
            for term in FIXTURE_TERMS:
                if term.lower() in text.lower():
                    findings.append(f"{relative}: fixture contains {term!r}")

        if is_under(path, ANALYSIS_DATA) or path == DERIVED_ROWS:
            for term in ANALYSIS_TERMS:
                if term.lower() in text.lower():
                    findings.append(f"{relative}: analysis data contains {term!r}")
            if RAW_BUILD.search(text):
                findings.append(f"{relative}: analysis data contains a raw build SHA")

    if findings:
        print("Publication audit failed:")
        for finding in findings:
            print(f"- {finding}")
        return 1

    print("Publication audit passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
