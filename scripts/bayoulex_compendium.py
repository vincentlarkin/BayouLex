"""
BayouLex Compendium.

Run with no arguments:

    python scripts/bayoulex_compendium.py

This is the small MVP console for the new API-first BayouLex workflow.
"""

from __future__ import annotations

import datetime as _dt
import hashlib
import json
import os
import shutil
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
PYTHON = sys.executable
DEFAULT_DATASET = ROOT / "data" / "bayoulex-content.sqlite"
DEFAULT_OFFLINE_ROOT = ROOT / "data" / "offline"
PACKS_DIR = ROOT / "packs"
DEFAULT_API_BASE = "http://127.0.0.1:5087/bayoulex/v1/"
IS_WINDOWS = sys.platform == "win32"
# One cross-platform desktop app (Avalonia) on Windows, Linux, and macOS.
CLIENT_NAME = "BayouLex desktop app"
CLIENT_PROJECT = "src/BayouLex.App/BayouLex.App.csproj"


def _dotnet() -> str:
    # Fall back to the per-user install location used by dotnet-install.sh.
    found = shutil.which("dotnet")
    if found:
        return found
    user_install = Path.home() / ".dotnet" / ("dotnet.exe" if IS_WINDOWS else "dotnet")
    return str(user_install) if user_install.exists() else "dotnet"


def _pause() -> None:
    input("\nPress Enter to return to the menu...")


def _dataset_version() -> str:
    default = _dt.datetime.now().strftime("%Y%m%d")
    raw = input(f"Dataset version [{default}]: ").strip()
    return raw or default


def _confirm(label: str) -> bool:
    print(f"\n{label}")
    print("This may download or process a lot of data.")
    answer = input("Continue? [y/N]: ").strip().lower()
    return answer in {"y", "yes"}


def _run(label: str, command: list[str], *, env: dict[str, str] | None = None) -> bool:
    print("\n" + "=" * 78)
    print(label)
    print("=" * 78)
    try:
        proc = subprocess.run(command, cwd=ROOT, env=env)
    except KeyboardInterrupt:
        print(f"\n[canceled] {label}")
        return False
    if proc.returncode == 0:
        print(f"\n[ok] {label}")
        return True
    print(f"\n[error] {label} failed. Exit code: {proc.returncode}")
    return False


def _has_dotnet_sdk() -> bool:
    try:
        proc = subprocess.run(
            [_dotnet(), "--list-sdks"],
            cwd=ROOT,
            capture_output=True,
            text=True,
            timeout=15,
        )
    except (OSError, subprocess.SubprocessError):
        return False
    return proc.returncode == 0 and bool(proc.stdout.strip())


def _require_dotnet_sdk() -> bool:
    if _has_dotnet_sdk():
        return True
    print(f"\n.NET SDK is required to run the BayouLex API or {CLIENT_NAME}.")
    print("Install the .NET SDK so the 'dotnet' command includes SDKs, then try again.")
    print("Check with: dotnet --list-sdks")
    return False


# Steps that may stop early (rate limits) without blocking the rest of the build.
OPTIONAL_STEPS = {"Download Louisiana Supreme Court decisions"}


def _download_from_source() -> None:
    if not _confirm("Download from official sources and build API data"):
        return

    version = _dataset_version()
    steps = [
        (
            "Download Louisiana codes, constitution, and legislative rules",
            [PYTHON, "scripts/download_louisiana_laws.py", "--categories", "all"],
        ),
        (
            "Download Louisiana Supreme Court decisions",
            [PYTHON, "scripts/download_louisiana_case_law.py"],
        ),
        (
            "Download Louisiana legislative bills and act text",
            [
                PYTHON,
                "scripts/download_louisiana_bills.py",
                "--session",
                "all",
                "--include-document-text",
                "--document-statuses",
                "law",
            ],
        ),
        (
            "Build API SQLite dataset",
            [
                PYTHON,
                "scripts/build_bayoulex_dataset.py",
                "--out",
                "out",
                "--db",
                str(DEFAULT_DATASET.relative_to(ROOT)),
                "--dataset-version",
                version,
            ],
        ),
        (
            "Build content packs",
            [PYTHON, "scripts/build_bayoulex_dataset.py", "--out", "out", "--packs", "packs"],
        ),
        (
            "Package offline API download",
            [
                PYTHON,
                "scripts/package_offline_snapshot.py",
                "--db",
                str(DEFAULT_DATASET.relative_to(ROOT)),
                "--dataset-version",
                version,
            ],
        ),
    ]
    for label, command in steps:
        if _run(label, command):
            continue
        if label in OPTIONAL_STEPS:
            # Justia rate-limits; whatever downloaded is kept and a re-run resumes the rest.
            print(f"[later] {label} is incomplete; re-run it later to fill in. Continuing.")
            continue
        return


def _build_offline_package() -> None:
    if not DEFAULT_DATASET.exists():
        print("\nNo API dataset found yet.")
        print("Choose 'Download from source and build API data' first.")
        return

    version = _dataset_version()
    _run(
        "Build offline package from current API dataset",
        [
            PYTHON,
            "scripts/package_offline_snapshot.py",
            "--db",
            str(DEFAULT_DATASET.relative_to(ROOT)),
            "--dataset-version",
            version,
        ],
    )


def _read_json(url: str) -> dict[str, Any]:
    request = urllib.request.Request(url, headers={"User-Agent": "BayouLex-Compendium/1.0"})
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))


def _sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def _download_file(url: str, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    request = urllib.request.Request(url, headers={"User-Agent": "BayouLex-Compendium/1.0"})
    with urllib.request.urlopen(request, timeout=120) as response, tmp.open("wb") as target:
        while True:
            block = response.read(1024 * 1024)
            if not block:
                break
            target.write(block)
    tmp.replace(path)


def _download_from_api() -> None:
    raw = input(f"API base URL [{DEFAULT_API_BASE}]: ").strip()
    base = (raw or DEFAULT_API_BASE).rstrip("/") + "/"
    target_root = ROOT / "data" / "api-downloads"

    try:
        init = _read_json(base + "init")
        version = str(init["datasetVersion"])
        manifest = _read_json(base + f"offline/{version}/manifest")
    except urllib.error.HTTPError as exc:
        print(f"\n[error] API returned HTTP {exc.code}. Is the API running and are offline chunks packaged?")
        return
    except Exception as exc:
        print(f"\n[error] Could not reach API: {exc}")
        return

    version_dir = target_root / version
    version_dir.mkdir(parents=True, exist_ok=True)
    (version_dir / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    chunks = manifest.get("chunks", [])
    print(f"\nDownloading {len(chunks)} chunk(s) for dataset {version} -> {version_dir}")
    for index, chunk in enumerate(chunks, start=1):
        name = str(chunk["fileName"])
        path = version_dir / name
        expected = str(chunk["sha256"])
        if path.exists() and _sha256(path) == expected:
            print(f"[skip] {index}/{len(chunks)} {name}")
            continue

        print(f"[get]  {index}/{len(chunks)} {name}")
        _download_file(base + f"offline/{version}/chunks/{chunk['number']}", path)
        actual = _sha256(path)
        if actual != expected:
            path.unlink(missing_ok=True)
            print(f"[error] Bad checksum for {name}")
            return

    print("\n[ok] API offline data downloaded and verified.")
    print(f"Files are in: {version_dir}")


def _run_api_smoke() -> None:
    if not _require_dotnet_sdk():
        return
    if not DEFAULT_DATASET.exists():
        print("\nNo API dataset found yet.")
        print("Choose 'Download from source' first, or copy bayoulex-content.sqlite into data/.")
        return

    env = os.environ.copy()
    env["BAYOULEX_DATASET_PATH"] = str(DEFAULT_DATASET)
    env["BAYOULEX_OFFLINE_ROOT"] = str(DEFAULT_OFFLINE_ROOT)
    env["BAYOULEX_PUBLIC_BASE_URL"] = DEFAULT_API_BASE.rstrip("/")
    print("\nStarting local BayouLex API.")
    print("Open this in a browser to verify it is alive:")
    print("  http://127.0.0.1:5087/bayoulex/v1/")
    print("Leave this terminal open while testing the C# app. Stop with Ctrl+C.\n")
    _run(
        "Run local API smoke server",
        [
            _dotnet(),
            "run",
            "--project",
            "src/BayouLex.Api/BayouLex.Api.csproj",
            "--urls",
            "http://127.0.0.1:5087",
        ],
        env=env,
    )


def _run_client() -> None:
    if not _require_dotnet_sdk():
        return
    print(f"\nStarting the {CLIENT_NAME}.")
    env = os.environ.copy()
    if (PACKS_DIR / "packs.json").exists():
        # The Library Manager installs from the packs built here instead of the published ones.
        env.setdefault("BAYOULEX_PACKS", str(PACKS_DIR))
        print(f"Packs: {PACKS_DIR} (File > Library Manager installs from here)")
    else:
        print("No local packs; the Library Manager uses the published packs.")
    _run(
        f"Run {CLIENT_NAME}",
        [_dotnet(), "run", "--project", CLIENT_PROJECT],
        env=env,
    )


def _explain() -> None:
    print(
        """
How BayouLex content flows
--------------------------
1. Download from source
   Fetches the official Louisiana material into out/ (codes and statutes from
   legis.la.gov, bills and act text, Supreme Court opinions from Justia), then
   builds data/bayoulex-content.sqlite and the content packs in packs/.

2. Content packs
   The desktop app installs packs: Louisiana Law (bundled with the app) plus
   optional Legislative Bills (one per year) and Supreme Court Decisions (one per
   decade). Packs are published to the GitHub release "library"; the app's
   Library Manager downloads and updates them.

3. Automatic updates
   The "Library packs" GitHub workflow refreshes this year's bills daily and the
   codes weekly, and publishes only packs whose content changed.

4. Desktop app
   Runs on the packs it has installed, fully offline.
"""
    )


def _status() -> None:
    print("\nCurrent BayouLex MVP status")
    print("-" * 30)
    print(f"API dataset:     {'present' if DEFAULT_DATASET.exists() else 'missing'}  {DEFAULT_DATASET}")
    print(f"Offline chunks:  {'present' if DEFAULT_OFFLINE_ROOT.exists() else 'missing'}  {DEFAULT_OFFLINE_ROOT}")
    print(f"API project:     present  {ROOT / 'src' / 'BayouLex.Api'}")
    print(f"Client:          present  {ROOT / CLIENT_PROJECT}")
    if DEFAULT_DATASET.exists() and not DEFAULT_OFFLINE_ROOT.exists():
        print("\nOffline chunks are optional. Choose option 2 to create them from the current dataset.")
    print("\nLegacy Python GUI/search files are intentionally not part of this branch.")


def _build_packs() -> bool:
    return _run(
        "Build content packs",
        [PYTHON, "scripts/build_bayoulex_dataset.py", "--out", "out", "--packs", str(PACKS_DIR.relative_to(ROOT))],
    )


def _publish_packs() -> None:
    if not (PACKS_DIR / "packs.json").exists():
        print("\nNo packs built yet. Choose 'Build content packs' first.")
        return
    _run("Preview what would be published", [PYTHON, "scripts/publish_packs.py", "--packs", str(PACKS_DIR), "--dry-run"])
    if input("\nPublish these to the GitHub release? [y/N]: ").strip().lower() in {"y", "yes"}:
        _run("Publish content packs", [PYTHON, "scripts/publish_packs.py", "--packs", str(PACKS_DIR)])


def main() -> int:
    while True:
        print("\nBayouLex Compendium")
        print("===================")
        print("1. Download from source and build everything")
        print("2. Build content packs from downloaded data")
        print("3. Publish content packs to GitHub")
        print(f"4. Run the {CLIENT_NAME}")
        print("5. Run local API smoke server")
        print("6. Build API offline package from current dataset")
        print("7. Download API offline data")
        print("8. Explain how content flows")
        print("9. Show status")
        print("0. Exit")
        choice = input("\nChoose: ").strip()

        try:
            actions = {
                "1": _download_from_source,
                "2": _build_packs,
                "3": _publish_packs,
                "4": _run_client,
                "5": _run_api_smoke,
                "6": _build_offline_package,
                "7": _download_from_api,
                "8": _explain,
                "9": _status,
            }
            if choice == "0":
                return 0
            if choice in actions:
                actions[choice]()
                _pause()
            else:
                print("Choose a number from 0 to 9.")
        except KeyboardInterrupt:
            print("\nCanceled. Back at the main menu.")


if __name__ == "__main__":
    raise SystemExit(main())
