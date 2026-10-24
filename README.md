# BayouLex

API-first Louisiana legal research compendium.

![BayouLex brand image](assets/branding/bayoulex-brand.png)

Source TOC: https://www.legis.la.gov/legis/LawsContents.aspx
Official Supreme Court opinions archive: https://www.lasc.org/CourtActions/2026
Pre-2000 Supreme Court year index: https://law.justia.com/cases/louisiana/supreme-court/

## What This Repo Produces

- Local law text and metadata (`sections/*.txt`, `sections/*.json`)
- Local Louisiana Supreme Court opinion PDFs plus extracted text/metadata
- Local Louisiana legislative bill histories, final disposition metadata, and extracted act text
- API-ready canonical SQLite dataset (`data/bayoulex-content.sqlite`)
- Offline API download chunks (`data/offline/<version>/`)
- BayouLex desktop app: an offline Louisiana law library reader (Windows, Linux, macOS)
- Optional read-only API over the same dataset

Default output root: `out/`

## Setup

Windows:

```powershell
python -m pip install -r requirements.txt
```

Linux / macOS:

```bash
python3 -m venv .venv
.venv/bin/pip install -r requirements.txt
```

Notes:
- The desktop app and API need the .NET 10 SDK as `dotnet` on PATH
  (Fedora: `sudo dnf install dotnet-sdk-10.0`; or a per-user install via
  `dotnet-install.sh --channel 10.0` into `~/.dotnet`).
- Source downloads use plain HTTP (no browser needed).
- Bundle PDFs are optional (`download_louisiana_laws.py --pdf`) and are the only
  thing that needs Playwright; the API dataset does not use them.

## Simple Compendium Menu

Normal use should start here:

```powershell
python scripts\bayoulex_compendium.py
```

On Linux/macOS: `.venv/bin/python scripts/bayoulex_compendium.py`.

The menu can download from official sources, build the canonical API/client
SQLite dataset, package offline chunks from an existing dataset, test offline
download from an API, run a local API smoke server, and launch the desktop app.

For the plain-English API/client flow, see [docs/QUICKSTART.md](docs/QUICKSTART.md).

## Scripts

- `scripts/download_louisiana_laws.py`
  - Scrapes TOC, downloads laws, writes local source files.
  - Default category: `revised-statutes`.
- `scripts/download_louisiana_case_law.py`
  - Downloads Louisiana Supreme Court opinions.
  - Uses the official Louisiana Supreme Court archive for `2000+`.
  - Uses Justia for older Supreme Court years exposed there (`1950-1999` plus `1885`).
  - Saves yearly bundles, local opinion PDFs when available, and extracted full text for indexing.
- `scripts/download_louisiana_bills.py`
  - Downloads bill metadata and printable bill histories from the official Louisiana Legislature session records.
  - Groups bills by session/chamber for browsing and indexes their outcome status.
  - Can extract official bill/act PDF text in memory without saving the PDFs.
- `scripts/build_bayoulex_dataset.py`
  - Builds the canonical SQLite library used by the desktop app and API, including the
    browsable outline (Book / Title / Chapter / Section) derived from the official headings.
- `scripts/package_offline_snapshot.py`
  - Compresses and chunks the SQLite dataset for API-served offline download.
- `scripts/bayoulex_compendium.py`
  - Human-facing MVP menu for source refresh, API download, API smoke, and desktop app launch.

The legacy Python GUI/search files live on the pushed `legacy` branch, not on
this API-first refactor branch.

## Project Layout

- `scripts/` - source ingestion, dataset building, offline packaging, and compendium menu.
- `src/BayouLex.Api/` - ASP.NET Core read-only API over the canonical SQLite dataset.
- `src/BayouLex.App/` - the BayouLex desktop app (Avalonia, cross-platform): contents tree,
  full-text search, citation links, tabs, bookmarks, and history over a local library file.
- `src/BayouLex.Api/` - optional ASP.NET Core read-only API over the same dataset.
- `src/BayouLex.Shared/` - API DTOs and API client.
- `out/` - generated source corpus used as build input.
- `.toc-cache/` - cached TOC snapshots used to speed resume runs.
- `requirements.txt` - Python dependencies for source ingestion and offline packaging.
- `.gitignore` - ignores generated caches/test artifacts and Python cache files.

## Common Commands

You usually only need the compendium:

```powershell
python scripts\bayoulex_compendium.py
```

The commands below are lower-level troubleshooting commands.

Download default (Revised Statutes):

```powershell
python scripts\download_louisiana_laws.py
```

Download everything:

```powershell
python scripts\download_louisiana_laws.py --categories all
```

Download Louisiana Supreme Court opinions (all supported years by default):

```powershell
python scripts\download_louisiana_case_law.py
```

Download a narrower Supreme Court range:

```powershell
python scripts\download_louisiana_case_law.py --years 2020-2026
```

Download the pre-2000 Justia-backed range only:

```powershell
python scripts\download_louisiana_case_law.py --years 1885,1950-1999
```

Download 2025 Regular Session bills:

```powershell
python scripts\download_louisiana_bills.py --session 25RS
```

Download all bill sessions exposed by the Louisiana Legislature site:

```powershell
python scripts\download_louisiana_bills.py --session all
```

Download bills and extract official act text for passed bills:

```powershell
python scripts\download_louisiana_bills.py --session all --include-document-text --document-statuses law
```

Re-run one bundle:

```powershell
python scripts\download_louisiana_laws.py --categories revised-statutes --bundle-regex "^TITLE 9 "
```

Build the canonical API/client dataset:

```powershell
python scripts\build_bayoulex_dataset.py --out out --db data\bayoulex-content.sqlite --dataset-version 20260426
```

Package offline snapshot chunks:

```powershell
python scripts\package_offline_snapshot.py --db data\bayoulex-content.sqlite --dataset-version 20260426
```

Run the desktop app on a library you built (otherwise it uses the last library
opened, or `~/.local/share/BayouLex/bayoulex-content.sqlite` /
`%LOCALAPPDATA%\BayouLex\bayoulex-content.sqlite`):

```bash
BAYOULEX_LIBRARY=data/bayoulex-content.sqlite dotnet run --project src/BayouLex.App
```

Or choose `Run the BayouLex desktop app` in the compendium. Bookmarks, history,
and settings live in that same per-user BayouLex folder.

Raw text search without SQLite:

```powershell
rg -n -S "capital punishment" out\
```

## Categories

`revised-statutes`, `louisiana-constitution`, `constitution-ancillaries`, `childrens-code`, `civil-code`, `code-of-civil-procedure`, `code-of-criminal-procedure`, `code-of-evidence`, `house-rules`, `senate-rules`, `joint-rules`, `all`

Additional indexed source after running the case-law downloader:

- `Louisiana Supreme Court Decisions`

Additional indexed source after running the bills downloader:

- `Louisiana Legislative Bills`

Notes:

- The Louisiana Constitution is already supported by `scripts\download_louisiana_laws.py` via `--categories louisiana-constitution`.
- Supreme Court decisions are sourced from the official Louisiana Supreme Court archive for `2000+` and from Justia for the older years that Justia exposes.

## Repo Hygiene

Generated caches/test artifacts are now ignored:

- `.toc-cache/`
- `out_test/`
- `out_cache_test/`
- `playwright-browsers/`
- `.venv/`
- Python cache files (`__pycache__`, `*.pyc`)

