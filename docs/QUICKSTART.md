# BayouLex Quickstart

## Using the app

1. Download BayouLex for your system from the repository's Releases and run `bayoulex`.
2. Louisiana Law is set up on first launch (it ships inside the app), so the library
   works offline right away.
3. The welcome window offers Legislative Bills (one pack per year) and Supreme Court
   Decisions (one pack per decade). Pick what you want; change it later from
   **File › Library Manager**.
4. When newer packs are published, the status bar shows **Updates available**.

Getting around: Ctrl+G jumps to a citation (`R.S. 9:2800`, `C.C. art. 2315`,
`Const. art. I, §2`, `HB 1`), Ctrl+F searches everything, Alt+←/→ go back and forward,
Alt+↑/↓ step through sections, Ctrl+D bookmarks, Ctrl+click opens a citation in a new tab.

## Working on the content

Prerequisites: Python with `requirements.txt` installed, and the .NET 10 SDK as `dotnet`.

Everything is reachable from the compendium menu:

```bash
python scripts/bayoulex_compendium.py      # .venv/bin/python on Linux/macOS
```

- **1. Download from source and build everything**: fetches the official material into
  `out/`, then builds `data/bayoulex-content.sqlite` and the content packs in `packs/`.
  The Supreme Court step is rate-limited by Justia; if it stops early the rest still
  builds, and re-running later fills in the missing years.
- **2. Build content packs**: rebuilds `packs/` from what is already in `out/`.
- **3. Publish content packs**: previews, then uploads changed packs to the `library`
  GitHub release that the app downloads from.
- **4. Run the desktop app**: the Library Manager installs from your local `packs/`.

## How the pieces fit

- `out/`: the downloaded source corpus (one folder per code, session, or year).
- `packs/`: what users install. Each pack is a compressed SQLite file without a
  search index; the app indexes it on install in seconds.
- The **Library packs** GitHub workflow refreshes this year's bills daily and the codes
  weekly, publishing only packs whose content changed.
- The **App release** workflow builds Windows, Linux, and macOS downloads when a `v*`
  tag is pushed, with the current Louisiana Law pack inside.

## The API

`src/BayouLex.Api` is an optional read-only HTTP API over `data/bayoulex-content.sqlite`
(compendium option 5 runs it locally on http://127.0.0.1:5087/bayoulex/v1/). The desktop
app does not need it. See `docs/NAS_DEPLOYMENT.md` for hosting it.
