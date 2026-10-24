"""
Publish content packs to the BayouLex GitHub release that the app downloads from.

Run after `build_bayoulex_dataset.py --packs packs`:

    python scripts/publish_packs.py --packs packs

Only packs whose content changed are uploaded (compared by content fingerprint, not build
time), and a pack is never replaced by one with noticeably fewer documents than the published
copy, so a partial or rate-limited download can't shrink what users have. The manifest
(packs.json) is uploaded last and keeps entries for packs not rebuilt this run.
Requires the GitHub CLI (`gh`) logged in with access to the repository.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tempfile
from pathlib import Path

DEFAULT_REPO = "vincentlarkin/BayouLex"
DEFAULT_TAG = "library"
RELEASE_NOTES = (
    "Content packs for the BayouLex desktop app (Louisiana law, legislative bills, court decisions). "
    "The app downloads these itself from File > Library Manager; packs.json lists what is available. "
    "This release is updated automatically."
)


def _gh(*args: str, check: bool = True, capture: bool = False) -> subprocess.CompletedProcess[str]:
    return subprocess.run(["gh", *args], check=check, text=True, capture_output=capture)


def _remote_manifest(repo: str, tag: str) -> dict[str, object]:
    with tempfile.TemporaryDirectory() as tmp:
        result = _gh("release", "download", tag, "--repo", repo, "--pattern", "packs.json", "--dir", tmp, check=False, capture=True)
        path = Path(tmp) / "packs.json"
        if result.returncode != 0 or not path.exists():
            return {"format": 1, "packs": []}
        return json.loads(path.read_text(encoding="utf-8"))


def _ensure_release(repo: str, tag: str) -> None:
    if _gh("release", "view", tag, "--repo", repo, check=False, capture=True).returncode == 0:
        return
    _gh(
        "release", "create", tag,
        "--repo", repo,
        "--title", "BayouLex Library Packs",
        "--notes", RELEASE_NOTES,
        "--latest=false",
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--packs", default="packs", help="Folder written by build_bayoulex_dataset.py --packs")
    parser.add_argument("--repo", default=DEFAULT_REPO)
    parser.add_argument("--tag", default=DEFAULT_TAG)
    parser.add_argument(
        "--min-ratio",
        type=float,
        default=0.95,
        help="Refuse to replace a published pack with one holding fewer than this share of its documents (default: 0.95)",
    )
    parser.add_argument("--dry-run", action="store_true", help="Show what would be uploaded without uploading")
    args = parser.parse_args(argv)

    packs_dir = Path(args.packs)
    local_path = packs_dir / "packs.json"
    if not local_path.exists():
        print(f"[error] {local_path} not found; build packs first", file=sys.stderr)
        return 1
    local = json.loads(local_path.read_text(encoding="utf-8"))
    remote = _remote_manifest(args.repo, args.tag)
    published = {str(p["id"]): p for p in remote.get("packs", [])}

    uploads: list[Path] = []
    merged = dict(published)
    for pack in local.get("packs", []):
        pack_id = str(pack["id"])
        archive = packs_dir / str(pack["file"])
        if not archive.exists():
            continue  # listed from an earlier build, not rebuilt this run
        current = published.get(pack_id)
        if current and current.get("content_sha256") and current.get("content_sha256") == pack.get("content_sha256"):
            print(f"[same]   {pack_id}: content unchanged, keeping published {current.get('version')}")
            continue
        if current and int(pack.get("documents") or 0) < args.min_ratio * int(current.get("documents") or 0):
            print(
                f"[refuse] {pack_id}: {pack.get('documents'):,} documents vs {current.get('documents'):,} published "
                "(incomplete download?); keeping the published pack"
            )
            continue
        print(f"[upload] {pack_id} {pack.get('version')}: {int(pack['bytes']) / 1e6:.1f} MB, {pack.get('documents'):,} documents")
        uploads.append(archive)
        merged[pack_id] = pack

    if not uploads:
        print("[ok] nothing changed; release left as is")
        return 0

    manifest = {
        "format": local.get("format", 1),
        "generated_at": local.get("generated_at"),
        "packs": sorted(merged.values(), key=_manifest_order),
    }
    if args.dry_run:
        print(f"[dry-run] would upload {len(uploads)} pack(s) and packs.json")
        return 0

    _ensure_release(args.repo, args.tag)
    _gh("release", "upload", args.tag, "--repo", args.repo, "--clobber", *[str(p) for p in uploads])
    with tempfile.TemporaryDirectory() as tmp:
        manifest_path = Path(tmp) / "packs.json"
        manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        # Upload the manifest last so it never points at a file that isn't there yet.
        _gh("release", "upload", args.tag, "--repo", args.repo, "--clobber", str(manifest_path))
    print(f"[ok] published {len(uploads)} pack(s) to {args.repo} release '{args.tag}'")
    return 0


def _manifest_order(pack: dict[str, object]) -> tuple[int, int, str]:
    group_rank = {"Louisiana Law": 0, "Legislative Bills": 1, "Supreme Court Decisions": 2}.get(str(pack.get("group")), 9)
    return group_rank, -int(pack.get("year") or 0), str(pack["id"])


if __name__ == "__main__":
    raise SystemExit(main())
