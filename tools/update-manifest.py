#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "SOURCE_MANIFEST.sha256"


def tracked_files() -> list[str]:
    raw = subprocess.check_output(
        ["git", "ls-files", "-z"],
        cwd=ROOT,
    )
    paths = raw.decode("utf-8").split("\0")
    return sorted(
        p for p in paths
        if p
        and p != "SOURCE_MANIFEST.sha256"
        and not any(part in {".git", ".vs", "bin", "obj"} for part in Path(p).parts)
    )


def main() -> int:
    lines: list[str] = []
    for rel in tracked_files():
        path = ROOT / rel
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        lines.append(f"{digest}  {rel}")

    content = "\n".join(lines) + "\n"
    old = MANIFEST.read_text(encoding="utf-8") if MANIFEST.exists() else ""
    if old != content:
        MANIFEST.write_text(content, encoding="utf-8", newline="\n")
        print(f"Updated {MANIFEST.name}: {len(lines)} entries")
    else:
        print(f"{MANIFEST.name} already current: {len(lines)} entries")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
