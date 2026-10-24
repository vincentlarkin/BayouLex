"""
Build the canonical BayouLex content database from generated downloader output.

Input:  out/**/bundle.json + sections/*.json + sections/*.txt, plus existing out/index.sqlite when present
Output: data/bayoulex-content.sqlite

This is the bridge away from many tiny runtime files. It preserves the existing
downloaders as ingestion tools while producing one API/client-ready SQLite DB.

It also derives a browsable outline (toc_nodes): Source -> bundle -> the
BOOK/TITLE/CHAPTER/... headings that the official site prints, centered, at the
top of the first section of each division.
"""

from __future__ import annotations

import argparse
import hashlib
import html
import json
import os
import re
import sqlite3
import sys
import time
from pathlib import Path
from urllib.parse import parse_qs, urlparse


def _doc_id_from_url(url: str) -> str:
    qs = parse_qs(urlparse(url).query)
    return (qs.get("d") or [""])[0]


def _doc_id_from_entry(entry: dict[str, object]) -> str:
    raw = entry.get("doc_id")
    if isinstance(raw, str) and raw.strip():
        return raw.strip()
    url = entry.get("url")
    if isinstance(url, str) and url.strip():
        return _doc_id_from_url(url)
    return ""


def _read_json(path: Path) -> dict[str, object]:
    try:
        raw = json.loads(path.read_text(encoding="utf-8"))
    except Exception:
        return {}
    return raw if isinstance(raw, dict) else {}


def _load_text(meta: dict[str, object], fallback_txt_path: Path) -> str:
    raw_text = meta.get("doc_text")
    if isinstance(raw_text, str) and raw_text.strip():
        return raw_text.strip()

    if not fallback_txt_path.exists():
        return ""

    raw = fallback_txt_path.read_text(encoding="utf-8", errors="replace")
    parts = raw.split("\n\n", 1)
    if len(parts) == 2:
        return parts[1].strip()
    return raw.strip()


def _find_bundle_paths(out_dir: Path) -> list[Path]:
    bundle_paths: list[Path] = []
    for root, dirs, files in os.walk(out_dir):
        dirs[:] = [
            d
            for d in dirs
            if d not in {"sections", "__pycache__"} and not d.startswith(".") and not d.endswith(".building")
        ]
        if "bundle.json" in files:
            bundle_paths.append(Path(root) / "bundle.json")
    return sorted(bundle_paths)


def _document_key(category: str, bundle: str, doc_id: str, url: str) -> str:
    raw = "\x1f".join([category, bundle, doc_id, url])
    return hashlib.sha256(raw.encode("utf-8", errors="replace")).hexdigest()[:24]


# Display order for sources in the outline; anything unlisted sorts after these.
CATEGORY_ORDER = [
    "Louisiana Constitution",
    "Constitution Ancillaries",
    "Civil Code",
    "Revised Statutes",
    "Code of Civil Procedure",
    "Code of Criminal Procedure",
    "Code of Evidence",
    "Children's Code",
    "Louisiana Legislative Bills",
    "Louisiana Supreme Court Decisions",
    "House Rules",
    "Senate Rules",
    "Joint Rules",
]
# Year/session bundles read best newest-first.
NEWEST_FIRST_CATEGORIES = {"Louisiana Legislative Bills", "Louisiana Supreme Court Decisions"}

# Division headings, highest level first. Same-or-higher rank closes the open division.
_HEADING_RANKS = [
    ("BOOK", 1),
    ("PRELIMINARY TITLE", 2),
    ("ARTICLE", 2),
    ("TITLE", 2),
    ("SUBTITLE", 3),
    ("CHAPTER", 4),
    ("SUBCHAPTER", 5),
    ("PART", 6),
    ("SUBPART", 7),
    ("SECTION", 8),
    ("SUBSECTION", 9),
    ("§", 10),
]
_HEADING_RE = re.compile(
    r"^(?:(" + "|".join(re.escape(k) for k, _ in _HEADING_RANKS if k != "§") + r")\b|(§)\s*\d)",
    re.IGNORECASE,
)
_RANK_BY_KEYWORD = dict(_HEADING_RANKS)
_PARA_RE = re.compile(r"<p\b([^>]*)>(.*?)</p>", re.IGNORECASE | re.DOTALL)
_CENTERED_RE = re.compile(r"""align\s*=\s*["']?center|text-align\s*:\s*center""", re.IGNORECASE)
_NATURAL_RE = re.compile(r"(\d+)")
# "CHAPTER 1 - X", "TITLE II -- X", "TITLE VIII--X", "BOOK I. X" -> keyword, number, rest
_DIVISION_PARTS_RE = re.compile(
    r"^(PRELIMINARY TITLE|[A-Z]+|§)\s*([0-9]+(?:\.[0-9]+)?(?:-?[A-Z])?|[IVXLC]+(?:-[A-Z])?(?![A-Za-z])|[A-Z](?![A-Za-z]))?\s*(?:\.|-{1,2}|—|:)?\s*(.*)$",
    re.IGNORECASE,
)
_ROMAN_RE = re.compile(r"^[IVXLC]+$")
_SMALL_WORDS = {"a", "an", "and", "as", "at", "by", "for", "in", "of", "on", "or", "the", "to", "upon", "with"}
# RS Title 9 mirrors the Civil Code with "CODE BOOK I", "CODE TITLE V" divisions.
_CODE_PREFIX_RE = re.compile(r"^CODE\s+(?=(?:BOOK|TITLE|PRELIMINARY)\b)", re.IGNORECASE)
# Reserved, empty divisions are marked "[BLANK]" / "(BLANK)"; "* * *" marks omitted text.
_BLANK_RE = re.compile(r"\s*[\[(]BLANK[\])]\s*", re.IGNORECASE)
_SECTION_TITLE_RE = re.compile(r"^\s*§\s*[0-9]+(?:\.[0-9]+)?[A-Z]?\.\s+(.+)$")


def _natural_key(value: str) -> list[object]:
    return [int(part) if part.isdigit() else part.lower() for part in _NATURAL_RE.split(value)]


def _heading_text(raw: str) -> str:
    return html.unescape(re.sub(r"<[^>]+>", "", raw)).replace("\xa0", " ")


def _collapse(text: str) -> str:
    return re.sub(r"\s+", " ", text).strip()


def _title_case(text: str) -> str:
    words = text.split(" ")
    out = []
    for i, word in enumerate(words):
        core = word.strip("(),;:")
        if _ROMAN_RE.match(core.rstrip(".")) and core.isupper() and len(core.rstrip(".")) > 1:
            out.append(word)
        elif i > 0 and core.lower() in _SMALL_WORDS and not out[-1].endswith((".", ":")):
            out.append(word.lower())
        else:
            out.append("-".join(_capitalize(part) for part in word.split("-")))
    return " ".join(out)


def _capitalize(word: str) -> str:
    """Capitalize the first letter even behind punctuation: '(BETWEEN' -> '(Between'."""
    lower = word.lower()
    for i, ch in enumerate(lower):
        if ch.isalpha():
            return lower[:i] + ch.upper() + lower[i + 1:]
    return lower


def _format_division(label: str) -> str:
    """'TITLE VIII--OF MINORS' -> 'Title VIII. Of Minors'."""
    m = _DIVISION_PARTS_RE.match(label)
    if not m:
        return _title_case(label)
    keyword, number, rest = m.group(1), m.group(2), m.group(3)
    if not number and keyword.upper() != "PRELIMINARY TITLE":
        return _title_case(label)  # "TITLE ON SALE AND THE COMMERCIAL LAWS" has no division number
    head = keyword if keyword == "§" else _title_case(keyword)
    if number:
        head = f"{head}{'' if keyword == '§' else ' '}{number}"
    rest = rest.strip(" .-—")
    return f"{head}. {_title_case(rest)}" if rest else head


def _leading_headings(doc_html: str) -> tuple[list[tuple[int, str]], int]:
    """
    Parse the centered heading paragraphs that open a section's HTML.

    Returns ([(rank, label)], line_count) where line_count is how many leading
    text lines those paragraphs occupy (headings can wrap across lines).
    """
    headings: list[tuple[int, str]] = []
    lines = 0
    for attrs, body in _PARA_RE.findall(doc_html or ""):
        if not _CENTERED_RE.search(attrs):
            break
        raw = _heading_text(body)
        lines += sum(1 for line in raw.splitlines() if line.strip())
        label = _collapse(_BLANK_RE.sub(" ", raw))
        if not label or set(label) <= {"*", " "}:
            continue
        m = _HEADING_RE.match(_CODE_PREFIX_RE.sub("", label))
        if m:
            keyword = (m.group(1) or m.group(2)).upper()
            headings.append((_RANK_BY_KEYWORD[keyword], label))
        elif headings:
            # A bare "BOOK II" gets its name on the next line; anything else just wrapped.
            rank, prev = headings[-1]
            bare = _DIVISION_PARTS_RE.match(prev)
            joiner = " — " if bare and not bare.group(3).strip(" .-—") else " "
            headings[-1] = (rank, prev + joiner + label)
    formatted = []
    for rank, label in headings:
        code = _CODE_PREFIX_RE.match(label)
        text = _format_division(_CODE_PREFIX_RE.sub("", label))
        formatted.append((rank, f"Code {text}" if code else text))
    return formatted, lines


def _same_division(label: str, bundle_name: str) -> bool:
    """'Title 9. Civil Code--Ancillaries' and bundle 'TITLE 9 - Civil Code-Ancillaries' are the same division."""
    def key(text: str) -> tuple[str, str]:
        parts = re.split(r"[\s.\-:]+", text.strip().lower(), maxsplit=2)
        number = parts[1] if len(parts) > 1 else ""
        if _ROMAN_RE.match(number.upper()):
            number = str(_roman_to_int(number.upper()))
        return parts[0], number
    return key(label) == key(bundle_name)


def _roman_to_int(numeral: str) -> int:
    values = {"I": 1, "V": 5, "X": 10, "L": 50, "C": 100}
    total = 0
    for i, ch in enumerate(numeral):
        value = values[ch]
        total += -value if i + 1 < len(numeral) and values[numeral[i + 1]] > value else value
    return total


def _bundle_label(bundle_name: str) -> str:
    """'TITLE 9 - Civil Code-Ancillaries' -> 'Title 9. Civil Code-Ancillaries'."""
    if _HEADING_RE.match(bundle_name):
        keyword, _, rest = bundle_name.partition(" ")
        number, _, name = rest.partition(" - ")
        if number and name:
            return f"{keyword.title()} {number}. {name}"
    return bundle_name


def _constitution_title(text: str, heading_lines: int) -> str:
    """Constitution entries are titled 'ARTICLE 1.1.'; the real title is on the '§1.  X' line."""
    following = [line.strip() for line in text.splitlines()[heading_lines:heading_lines + 3]]
    for line in following:
        m = _SECTION_TITLE_RE.match(line)
        if m:
            return _collapse(m.group(1)).rstrip(".")
    if following and following[0].isupper() and len(following[0]) <= 40:
        return _title_case(following[0])  # e.g. PREAMBLE
    return ""


class _Outline:
    def __init__(self, con: sqlite3.Connection) -> None:
        self._con = con
        self._sort = 0
        self._existing: dict[tuple[int | None, str], int] = {}

    def add(self, parent_id: int | None, category: str, kind: str, label: str) -> int:
        # Sections sometimes repeat their division heading; reuse the node instead of duplicating it.
        key = (parent_id, label.lower())
        if key in self._existing:
            return self._existing[key]
        self._sort += 1
        cur = self._con.execute(
            "INSERT INTO toc_nodes(parent_id, category, kind, label, sort_order) VALUES (?, ?, ?, ?, ?)",
            (parent_id, category, kind, label, self._sort),
        )
        self._existing[key] = int(cur.lastrowid)
        return self._existing[key]


def _schema(con: sqlite3.Connection) -> None:
    con.executescript(
        """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA temp_store = MEMORY;
        PRAGMA cache_size = -200000;

        CREATE TABLE dataset_metadata (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE documents (
            id INTEGER PRIMARY KEY,
            document_key TEXT NOT NULL UNIQUE,
            doc_id TEXT NOT NULL,
            category TEXT NOT NULL,
            bundle TEXT NOT NULL,
            session_id TEXT NOT NULL DEFAULT '',
            chamber TEXT NOT NULL DEFAULT '',
            status_group TEXT NOT NULL DEFAULT '',
            status_label TEXT NOT NULL DEFAULT '',
            citation TEXT NOT NULL DEFAULT '',
            title TEXT NOT NULL DEFAULT '',
            text TEXT NOT NULL,
            url TEXT NOT NULL DEFAULT '',
            local_file TEXT NOT NULL DEFAULT '',
            metadata_json TEXT NOT NULL DEFAULT '{}',
            source_path TEXT NOT NULL DEFAULT '',
            toc_node_id INTEGER,
            sort_order INTEGER NOT NULL DEFAULT 0,
            heading_lines INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE toc_nodes (
            id INTEGER PRIMARY KEY,
            parent_id INTEGER REFERENCES toc_nodes(id),
            category TEXT NOT NULL,
            kind TEXT NOT NULL,
            label TEXT NOT NULL,
            sort_order INTEGER NOT NULL
        );

        CREATE INDEX idx_toc_nodes_parent ON toc_nodes(parent_id, sort_order);
        CREATE INDEX idx_documents_toc ON documents(toc_node_id, sort_order);
        CREATE INDEX idx_documents_citation ON documents(citation COLLATE NOCASE);

        CREATE INDEX idx_documents_category_bundle ON documents(category, bundle);
        CREATE INDEX idx_documents_status ON documents(status_label);
        CREATE INDEX idx_documents_doc_id ON documents(doc_id);

        CREATE VIRTUAL TABLE documents_fts USING fts5(
            citation,
            title,
            text,
            content='documents',
            content_rowid='id',
            tokenize='unicode61',
            prefix='2 3 4 5'
        );
        """
    )


def _insert_metadata(con: sqlite3.Connection, key: str, value: str) -> None:
    con.execute(
        "INSERT OR REPLACE INTO dataset_metadata(key, value) VALUES (?, ?)",
        (key, value),
    )


def _insert_document(
    con: sqlite3.Connection,
    *,
    category: str,
    bundle_name: str,
    doc_id: str,
    url: str,
    citation: str,
    title: str,
    text: str,
    session_id: str = "",
    chamber: str = "",
    status_group: str = "",
    status_label: str = "",
    local_file: str = "",
    metadata: dict[str, object] | None = None,
    source_path: str = "",
    toc_node_id: int | None = None,
    sort_order: int = 0,
    heading_lines: int = 0,
) -> int:
    key = _document_key(category, bundle_name, doc_id, url)
    cursor = con.execute(
        """
        INSERT OR IGNORE INTO documents(
            document_key, doc_id, category, bundle, session_id, chamber,
            status_group, status_label, citation, title, text, url,
            local_file, metadata_json, source_path, toc_node_id, sort_order, heading_lines
        )
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        """,
        (
            key,
            doc_id,
            category,
            bundle_name,
            session_id,
            chamber,
            status_group,
            status_label,
            citation,
            title,
            text,
            url,
            local_file,
            json.dumps(metadata or {}, ensure_ascii=False, separators=(",", ":")),
            source_path,
            toc_node_id,
            sort_order,
            heading_lines,
        ),
    )
    return 1 if cursor.rowcount > 0 else 0


def _merge_existing_index(con: sqlite3.Connection, index_path: Path) -> int:
    if not index_path.exists():
        return 0

    src = sqlite3.connect(index_path)
    src.row_factory = sqlite3.Row
    inserted = 0
    try:
        try:
            rows = src.execute(
                """
                SELECT doc_id, category, bundle, session_id, chamber, status_group, status_label,
                       citation, title, text, url, local_file
                FROM docs_fts
                WHERE COALESCE(text, '') <> ''
                """
            )
        except sqlite3.Error:
            return 0

        for row in rows:
            category = str(row["category"] or "").strip()
            bundle_name = str(row["bundle"] or "").strip()
            doc_id = str(row["doc_id"] or "").strip()
            text = str(row["text"] or "").strip()
            if not category or not bundle_name or not doc_id or not text:
                continue
            inserted += _insert_document(
                con,
                category=category,
                bundle_name=bundle_name,
                doc_id=doc_id,
                url=str(row["url"] or "").strip(),
                citation=str(row["citation"] or "").strip(),
                title=str(row["title"] or "").strip(),
                text=text,
                session_id=str(row["session_id"] or "").strip(),
                chamber=str(row["chamber"] or "").strip(),
                status_group=str(row["status_group"] or "").strip(),
                status_label=str(row["status_label"] or "").strip(),
                local_file=str(row["local_file"] or "").strip(),
                source_path=str(index_path),
            )
    finally:
        src.close()

    return inserted


def build_dataset(out_dir: Path, db_path: Path, dataset_version: str, existing_index: Path | None) -> int:
    bundle_paths = _find_bundle_paths(out_dir)
    if not bundle_paths:
        raise RuntimeError(f"No bundle.json files found under {out_dir}")

    tmp_path = db_path.with_name(db_path.name + ".building")
    for path in [tmp_path, tmp_path.with_name(tmp_path.name + "-wal"), tmp_path.with_name(tmp_path.name + "-shm")]:
        if path.exists():
            path.unlink()

    db_path.parent.mkdir(parents=True, exist_ok=True)
    con = sqlite3.connect(tmp_path)
    total = 0
    try:
        _schema(con)
        with con:
            _insert_metadata(con, "dataset_version", dataset_version)
            _insert_metadata(con, "built_at_epoch", str(int(time.time())))
            _insert_metadata(con, "source_out_dir", str(out_dir.resolve()))

            bundles: list[tuple[Path, str, str, list[object]]] = []
            for bundle_path in bundle_paths:
                bundle = _read_json(bundle_path)
                category = str(bundle.get("category_name") or "").strip()
                bundle_name = str(bundle.get("bundle_name") or bundle_path.parent.name).strip()
                entries = bundle.get("entries") or []
                if not category or not isinstance(entries, list):
                    continue
                bundles.append((bundle_path.parent, category, bundle_name, entries))

            def _category_key(name: str) -> tuple[int, str]:
                rank = CATEGORY_ORDER.index(name) if name in CATEGORY_ORDER else len(CATEGORY_ORDER)
                return rank, name.lower()

            outline = _Outline(con)
            by_category: dict[str, list[tuple[Path, str, list[object]]]] = {}
            for bundle_dir, category, bundle_name, entries in bundles:
                by_category.setdefault(category, []).append((bundle_dir, bundle_name, entries))

            doc_sort = 0
            for category in sorted(by_category, key=_category_key):
                category_bundles = sorted(
                    by_category[category],
                    key=lambda item: _natural_key(item[1]),
                    reverse=category in NEWEST_FIRST_CATEGORIES,
                )
                category_node = outline.add(None, category, "source", category)

                for bundle_dir, bundle_name, entries in category_bundles:
                    # A flat source (one bundle named like the source) needs no extra level.
                    bundle_node = (
                        category_node
                        if bundle_name == category
                        else outline.add(category_node, category, "bundle", _bundle_label(bundle_name))
                    )
                    open_divisions: list[tuple[int, int]] = []  # (rank, node id)

                    for entry in entries:
                        if not isinstance(entry, dict):
                            continue
                        doc_id = _doc_id_from_entry(entry)
                        if not doc_id:
                            continue
                        meta_path = bundle_dir / "sections" / f"{doc_id}.json"
                        txt_path = bundle_dir / "sections" / f"{doc_id}.txt"
                        meta = _read_json(meta_path)
                        text = _load_text(meta, txt_path)
                        if not text:
                            continue

                        headings, heading_lines = _leading_headings(str(meta.get("doc_html") or ""))
                        for rank, label in headings:
                            while open_divisions and open_divisions[-1][0] >= rank:
                                open_divisions.pop()
                            if (
                                not open_divisions
                                and bundle_node != category_node
                                and _same_division(label, bundle_name)
                            ):
                                # "TITLE 1 GENERAL PROVISIONS" inside bundle "TITLE 1 - General Provisions".
                                open_divisions.append((rank, bundle_node))
                                continue
                            parent = open_divisions[-1][1] if open_divisions else bundle_node
                            open_divisions.append((rank, outline.add(parent, category, "division", label)))
                        doc_sort += 1

                        url = str(meta.get("url") or entry.get("url") or "").strip()
                        citation = str(meta.get("citation") or entry.get("citation") or "").strip()
                        title = str(meta.get("title") or entry.get("title") or "").strip()
                        if category == "Louisiana Constitution" and title.upper().startswith("ARTICLE"):
                            title = _constitution_title(text, heading_lines) or title
                        session_id = str(meta.get("session_name") or meta.get("session_id") or "").strip()
                        chamber = str(meta.get("chamber_label") or meta.get("chamber") or "").strip()
                        status_group = str(meta.get("bill_status_group") or "").strip()
                        status_label = str(meta.get("bill_status_label") or "").strip()
                        local_file = str(meta.get("local_file") or "").strip()
                        total += _insert_document(
                            con,
                            category=category,
                            bundle_name=bundle_name,
                            doc_id=doc_id,
                            url=url,
                            citation=citation,
                            title=title,
                            text=text,
                            session_id=session_id,
                            chamber=chamber,
                            status_group=status_group,
                            status_label=status_label,
                            local_file=local_file,
                            metadata=meta,
                            source_path=str(meta_path.relative_to(out_dir)) if meta_path.exists() else "",
                            toc_node_id=open_divisions[-1][1] if open_divisions else bundle_node,
                            sort_order=doc_sort,
                            heading_lines=heading_lines,
                        )

            if existing_index is not None:
                merged = _merge_existing_index(con, existing_index)
                total += merged
                _insert_metadata(con, "merged_existing_index_rows", str(merged))

            con.execute("INSERT INTO documents_fts(documents_fts) VALUES ('rebuild')")
            _insert_metadata(con, "document_count", str(total))

        con.execute("PRAGMA optimize;")
        con.execute("PRAGMA wal_checkpoint(TRUNCATE);")
    finally:
        con.close()

    if total <= 0:
        tmp_path.unlink(missing_ok=True)
        raise RuntimeError("Built 0 documents; refusing to replace dataset")

    os.replace(tmp_path, db_path)
    for path in [tmp_path.with_name(tmp_path.name + "-wal"), tmp_path.with_name(tmp_path.name + "-shm")]:
        path.unlink(missing_ok=True)
    return total


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", default="out", help="Generated downloader output root")
    parser.add_argument("--db", default="data/bayoulex-content.sqlite", help="Output SQLite database")
    parser.add_argument("--dataset-version", default=time.strftime("%Y%m%d"), help="Immutable dataset version label")
    parser.add_argument(
        "--existing-index",
        default=None,
        help="Existing docs_fts SQLite index to merge for content not present in sidecar files (default: <out>/index.sqlite when present)",
    )
    args = parser.parse_args(argv)

    try:
        out_dir = Path(args.out)
        existing_index = Path(args.existing_index) if args.existing_index else out_dir / "index.sqlite"
        if not existing_index.exists():
            existing_index = None
        total = build_dataset(out_dir, Path(args.db), str(args.dataset_version), existing_index)
    except Exception as exc:
        print(f"[error] {exc}", file=sys.stderr)
        return 1

    print(f"[ok] built {total:,} documents -> {args.db}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
