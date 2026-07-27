"""Check that local links in tracked Markdown files resolve."""

import re
import subprocess
import urllib.parse
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
LINK = re.compile(r"!?\[[^\]]*\]\(([^)]+)\)")
TEMPLATE_ROOT = ROOT / "src" / "ThroughlineBuild.Commands" / "Templates"


def tracked_markdown() -> list[Path]:
    result = subprocess.run(
        ["git", "ls-files", "-z", "*.md"],
        cwd=ROOT,
        check=True,
        capture_output=True,
    )
    return [
        ROOT / entry.decode("utf-8")
        for entry in result.stdout.split(b"\0")
        if entry
    ]


def link_target(raw: str) -> str:
    target = raw.strip()
    if target.startswith("<") and ">" in target:
        target = target[1 : target.index(">")]
    elif " " in target:
        target = target.split(" ", 1)[0]
    return urllib.parse.unquote(target).split("#", 1)[0]


def resolve(source: Path, target: str) -> Path:
    if target.startswith("/"):
        return ROOT / target.lstrip("/")
    if source.parent == TEMPLATE_ROOT:
        return ROOT / "docs" / target
    return source.parent / target


def main() -> int:
    failures: list[str] = []
    for source in tracked_markdown():
        if not source.is_file():
            continue
        text = source.read_text(encoding="utf-8")
        for line_number, line in enumerate(text.splitlines(), start=1):
            for match in LINK.finditer(line):
                raw = match.group(1)
                if raw.startswith(("http://", "https://", "mailto:", "#")):
                    continue
                target = link_target(raw)
                if not target or any(char in target for char in "<>{}*"):
                    continue
                resolved = resolve(source, target)
                if not resolved.exists():
                    relative = source.relative_to(ROOT).as_posix()
                    failures.append(f"{relative}:{line_number}: {raw}")

    if failures:
        print("Broken local Markdown links:")
        for failure in failures:
            print(f"- {failure}")
        return 1

    print("Markdown link check passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
