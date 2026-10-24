# BayouLex

A desktop library of Louisiana law: the Constitution, every code, the Revised
Statutes, legislative bills, and Supreme Court decisions, readable and searchable
offline on Windows, Linux, and macOS.

![BayouLex brand image](assets/branding/bayoulex-brand.png)

## Getting BayouLex

Download the build for your system from the repository's Releases and run `bayoulex`.
Louisiana Law comes with the app, so it works offline from the first launch. A
welcome window then offers the optional collections; you can change them any time
from **File › Library Manager**:

| Collection | Packs | Contents |
|---|---|---|
| Louisiana Law (included) | `louisiana-law` | Constitution, Civil Code, Revised Statutes, codes of procedure and evidence, Children's Code, legislative rules |
| Legislative Bills | `bills-<year>` | Every bill and resolution: history, votes, official documents, enacted act text |
| Supreme Court Decisions | `supreme-court-<decade>s` | Louisiana Supreme Court opinions |

The app checks for newer packs when it starts and offers them in the status bar.

## How Content Stays Current

- Packs are published to the `library` GitHub release with a `packs.json` manifest
  (version, size, checksum, and a content fingerprint for each pack).
- The **Library packs** workflow (`.github/workflows/library-packs.yml`) runs daily:
  it refreshes this year's bills every day and the codes and statutes on Mondays,
  then uploads only packs whose content actually changed. Run it by hand with a
  plan such as `bills-2019,bills-2020` or `court-2010-2019` to backfill.
- The publisher never replaces a pack with one holding noticeably fewer documents,
  so a partial or rate-limited download can't shrink what users have.
- Tagging `v*` runs the **App release** workflow, which builds Windows, Linux, and
  macOS downloads with the current Louisiana Law pack inside.

Sources: https://www.legis.la.gov/legis/LawsContents.aspx (codes, statutes, bills) and
https://law.justia.com/cases/louisiana/supreme-court/ (Supreme Court opinions).

## What This Repo Produces

- Content packs for the app (`packs/<pack>.sqlite.br` + `packs/packs.json`)
- An all-in-one SQLite library for development and the API (`data/bayoulex-content.sqlite`)
- The downloaded source corpus the packs are built from (`out/`)
- The BayouLex desktop app (`src/BayouLex.App`) and an optional read-only API (`src/BayouLex.Api`)

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

The menu downloads from the official sources, builds the library and content
packs, publishes packs to GitHub, and launches the desktop app (which then
installs from your locally built packs).

For the plain-English API/client flow, see [docs/QUICKSTART.md](docs/QUICKSTART.md).

## Scripts

- `scripts/download_louisiana_laws.py`
  - Scrapes TOC, downloads laws, writes local source files.
  - Default category: `revised-statutes`.
- `scripts/download_louisiana_case_law.py`
  - Downloads Louisiana Supreme Court opinions.
  - Uses Justia for every year it exposes (`1950`-present plus `1885`). lasc.org moved to a
    Blazor app that can't be read without a browser.
  - Justia rate-limits: requests are throttled (`--request-interval`), 429s back off, and years
    that fail are skipped and listed so a later re-run fills them in. Treat this step as
    "download later" if it stops early; the compendium keeps going without it.
  - Saves yearly bundles, local opinion PDFs when available, and extracted full text for indexing.
- `scripts/download_louisiana_bills.py`
  - Downloads bill metadata and printable bill histories from the official Louisiana Legislature session records.
  - `--session` takes ids (`25RS,251ES`), a year (`2025`), `current`, or `all`.
  - Groups bills by session/chamber for browsing and indexes their outcome status.
  - Can extract official bill/act PDF text in memory without saving the PDFs.
- `scripts/build_bayoulex_dataset.py`
  - Builds the library, including the browsable outline (Book / Title / Chapter / Section)
    derived from the official headings and clean act text for bills.
  - Default: one all-in-one database (`data/bayoulex-content.sqlite`) for development and the API.
  - `--packs packs [--only louisiana-law,bills-2025]`: content packs for the app, without a search
    index (the app builds it on install), Brotli-compressed, plus `packs/packs.json`.
- `scripts/publish_packs.py`
  - Uploads changed packs and the merged manifest to the `library` GitHub release (`--dry-run` to preview).
- `scripts/refresh_packs.py`
  - Download + build + publish for a plan (`law`, `bills-current`, `bills-YYYY`, `court-YYYY-YYYY`);
    what the scheduled workflow runs.
- `scripts/package_offline_snapshot.py`
  - Compresses and chunks the SQLite dataset for API-served offline download.
- `scripts/bayoulex_compendium.py`
  - Menu for downloading, building packs, publishing, and launching the app.

The legacy Python GUI/search files live on the pushed `legacy` branch.

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

Build the all-in-one library (development, API):

```powershell
python scripts\build_bayoulex_dataset.py --out out --db data\bayoulex-content.sqlite --dataset-version 20260426
```

Build content packs for the app, preview, and publish:

```bash
python scripts/build_bayoulex_dataset.py --packs packs
python scripts/publish_packs.py --packs packs --dry-run
python scripts/publish_packs.py --packs packs
```

Point the app at locally built packs instead of the published ones:

```bash
BAYOULEX_PACKS=packs dotnet run --project src/BayouLex.App
```

Package offline snapshot chunks:

```powershell
python scripts\package_offline_snapshot.py --db data\bayoulex-content.sqlite --dataset-version 20260426
```

Open the all-in-one development library directly, bypassing packs:

```bash
BAYOULEX_LIBRARY=data/bayoulex-content.sqlite dotnet run --project src/BayouLex.App
```

Normally the app reads its pack library (`library.sqlite`) plus bookmarks, history,
and settings from `~/.local/share/BayouLex/` (`%LOCALAPPDATA%\BayouLex\` on Windows;
`BAYOULEX_DATA_DIR` overrides it).

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
- Supreme Court decisions are sourced from Justia for every year (see the case-law script notes above).

## Repo Hygiene

Generated caches/test artifacts are now ignored:

- `.toc-cache/`
- `out_test/`
- `out_cache_test/`
- `playwright-browsers/`
- `.venv/`
- Python cache files (`__pycache__`, `*.pyc`)

