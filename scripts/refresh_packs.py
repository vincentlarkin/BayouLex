"""
Refresh BayouLex content packs from the official sources and (optionally) publish them.

This is what the scheduled GitHub workflow runs; it works the same locally.

    python scripts/refresh_packs.py --plan law,bills-current
    python scripts/refresh_packs.py --plan bills-2019,bills-2020 --publish
    python scripts/refresh_packs.py --plan court-2010-2019 --publish

Plan items:
  law            every code, the Revised Statutes, the constitution, and legislative rules
  bills-current  this year's legislative sessions
  bills-YYYY     one year's legislative sessions
  court-YYYY-YYYY  Supreme Court opinions for a range of years (Justia rate-limits; partial
                   downloads are never published over a more complete pack)
"""

from __future__ import annotations

import argparse
import datetime as _dt
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PYTHON = sys.executable


def _run(label: str, command: list[str]) -> bool:
    print(f"\n=== {label}", flush=True)
    result = subprocess.run(command, cwd=ROOT)
    if result.returncode != 0:
        print(f"[warn] {label} exited with {result.returncode}", flush=True)
    return result.returncode == 0


def _steps(item: str) -> tuple[list[tuple[str, list[str]]], list[str], bool]:
    """(download steps, pack ids, must the downloads all succeed before building) for one plan item."""
    if item == "law":
        return (
            [("Download Louisiana law", [PYTHON, "scripts/download_louisiana_laws.py", "--categories", "all"])],
            ["louisiana-law"],
            True,
        )
    bills = re.fullmatch(r"bills-(current|\d{4})", item)
    if bills:
        year = _dt.datetime.now().year if bills.group(1) == "current" else int(bills.group(1))
        return (
            [(
                f"Download {year} bills",
                [
                    PYTHON, "scripts/download_louisiana_bills.py",
                    "--session", str(year),
                    "--include-document-text", "--document-statuses", "law",
                ],
            )],
            [f"bills-{year}"],
            True,
        )
    court = re.fullmatch(r"court-(\d{4})-(\d{4})", item)
    if court:
        start, end = int(court.group(1)), int(court.group(2))
        decades = sorted({year // 10 * 10 for year in range(start, end + 1)})
        return (
            [(f"Download Supreme Court {start}-{end}", [PYTHON, "scripts/download_louisiana_case_law.py", "--years", f"{start}-{end}"])],
            [f"supreme-court-{decade}s" for decade in decades],
            # Rate limits stop this early; build what arrived and let the publisher's size check decide.
            False,
        )
    raise SystemExit(f"Unknown plan item: {item}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--plan", default="law,bills-current", help="Comma-separated plan items (default: law,bills-current)")
    parser.add_argument("--packs", default="packs", help="Pack output folder (default: packs)")
    parser.add_argument("--publish", action="store_true", help="Publish changed packs to the GitHub release afterwards")
    args = parser.parse_args(argv)

    pack_ids: list[str] = []
    for item in [part.strip() for part in args.plan.split(",") if part.strip()]:
        steps, ids, strict = _steps(item)
        ok = all(_run(label, command) for label, command in steps)
        if ok or not strict:
            pack_ids.extend(ids)
        else:
            print(f"[skip] {item}: download failed, not rebuilding {', '.join(ids)}")

    if not pack_ids:
        print("[error] nothing to build", file=sys.stderr)
        return 1
    if not _run(
        "Build packs",
        [PYTHON, "scripts/build_bayoulex_dataset.py", "--packs", args.packs, "--only", ",".join(pack_ids)],
    ):
        return 1
    if args.publish and not _run("Publish packs", [PYTHON, "scripts/publish_packs.py", "--packs", args.packs]):
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
