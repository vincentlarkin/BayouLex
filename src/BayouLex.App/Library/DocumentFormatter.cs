using System.Text.RegularExpressions;

namespace BayouLex.App.Library;

public enum BlockKind
{
    DivisionHeading,
    Caption,
    Body,
    Note,
    SubHeading,
}

/// <summary>A run of text that may link to another document or an outside page.</summary>
public sealed record Segment(string Text, long? DocumentId = null, string? Url = null, bool Highlight = false);

public abstract record Block;

public sealed record TextBlockModel(BlockKind Kind, int Indent, IReadOnlyList<Segment> Segments) : Block;

public sealed record HistoryRow(string Date, string Chamber, string Reference, string Action);

public sealed record HistoryTableModel(IReadOnlyList<HistoryRow> Rows) : Block;

public sealed record LinkListModel(string Title, IReadOnlyList<(string Label, string Url)> Links) : Block;

public sealed record DocumentLayout(
    string CitationDisplay,
    string Title,
    IReadOnlyList<(string Label, string Value)> Facts,
    IReadOnlyList<Block> Blocks);

/// <summary>Turns a library document's plain text into blocks the reader can lay out.</summary>
public static partial class DocumentFormatter
{
    private const string ActTextMarker = "===== OFFICIAL ACT TEXT =====";

    public static DocumentLayout Format(LibraryDocument doc, LibraryDatabase library, IReadOnlyList<string> highlightTerms)
    {
        var facts = new List<(string, string)>();
        List<RawBlock> raw;
        if (doc.Category == "Louisiana Legislative Bills")
        {
            raw = FormatBill(doc, facts);
        }
        else if (doc.Category.Contains("Court", StringComparison.OrdinalIgnoreCase))
        {
            raw = FormatOpinion(doc, facts);
        }
        else
        {
            raw = FormatCodeSection(doc);
        }

        // Resolve every citation in one pass so links only appear for documents the library has.
        var found = new List<(RawBlock Block, IReadOnlyList<CitationSpan> Spans)>();
        foreach (var block in raw)
        {
            found.Add((block, block.Linkify ? Citations.Find(block.Text, doc.Citation) : []));
        }
        var resolved = library.ResolveCitations(found.SelectMany(item => item.Spans).Select(span => span.Key));
        var highlighter = BuildHighlighter(highlightTerms);

        var blocks = new List<Block>(raw.Count);
        foreach (var (block, spans) in found)
        {
            blocks.Add(block.Structured ?? new TextBlockModel(
                block.Kind,
                block.Indent,
                BuildSegments(block.Text, spans, resolved, doc.Citation, highlighter)));
        }

        var title = string.IsNullOrWhiteSpace(doc.Title) ? doc.Bundle : doc.Title;
        return new DocumentLayout(Citations.Display(doc.Citation), title, facts, blocks);
    }

    private sealed record RawBlock(BlockKind Kind, int Indent, string Text, bool Linkify = true, Block? Structured = null);

    private static List<RawBlock> FormatCodeSection(LibraryDocument doc)
    {
        var blocks = new List<RawBlock>();
        var lines = doc.Text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        var headingLines = Math.Min(doc.HeadingLines, lines.Count);
        for (var i = 0; i < headingLines; i++)
        {
            blocks.Add(new RawBlock(BlockKind.DivisionHeading, 0, lines[i], Linkify: false));
        }

        var captionSeen = false;
        var previousIndent = 0;
        foreach (var line in lines.Skip(headingLines))
        {
            // Hard-wrapped captions and paragraphs continue on a line that starts lower-case.
            if (blocks.Count > headingLines && char.IsLower(line[0]))
            {
                var last = blocks[^1];
                blocks[^1] = last with { Text = last.Text + " " + line };
                continue;
            }

            if (!captionSeen && CaptionRegex().IsMatch(line))
            {
                blocks.Add(new RawBlock(BlockKind.Caption, 0, line, Linkify: false));
                captionSeen = true;
                continue;
            }
            if (NoteRegex().IsMatch(line))
            {
                blocks.Add(new RawBlock(BlockKind.Note, 0, line));
                continue;
            }

            var indent = IndentFor(line, previousIndent);
            previousIndent = indent;
            blocks.Add(new RawBlock(BlockKind.Body, indent, line));
        }
        return blocks;
    }

    private static List<RawBlock> FormatBill(LibraryDocument doc, List<(string, string)> facts)
    {
        var meta = doc.Metadata;
        AddFact(facts, "Session", doc.SessionId);
        AddFact(facts, "Author", meta.GetValueOrDefault("author"));
        AddFact(facts, "Status", doc.StatusLabel);
        AddFact(facts, "Last action", meta.GetValueOrDefault("current_status"));

        var blocks = new List<RawBlock>();
        var text = doc.Text;
        var actIndex = text.IndexOf(ActTextMarker, StringComparison.Ordinal);
        var infoText = actIndex >= 0 ? text[..actIndex] : text;
        var lines = infoText.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();

        var tableStart = FindHistoryHeader(lines);
        var authorsAt = lines.IndexOf("Authors:");
        var documentsAt = lines.IndexOf("Available Documents:");

        if (tableStart >= 0)
        {
            var end = new[] { authorsAt, documentsAt, lines.Count }.Where(index => index > tableStart).Min();
            var rows = ParseHistory(lines.GetRange(tableStart + 5, end - tableStart - 5));
            if (rows.Count > 0)
            {
                blocks.Add(new RawBlock(BlockKind.SubHeading, 0, "History", Linkify: false));
                blocks.Add(new RawBlock(BlockKind.Body, 0, "", Structured: new HistoryTableModel(rows)));
            }
        }

        if (authorsAt >= 0)
        {
            var end = documentsAt > authorsAt ? documentsAt : lines.Count;
            var authors = lines.GetRange(authorsAt + 1, end - authorsAt - 1);
            if (authors.Count > 0)
            {
                facts.Add(("Authors", string.Join(", ", authors)));
            }
        }

        if (documentsAt >= 0)
        {
            blocks.Add(new RawBlock(BlockKind.SubHeading, 0, "Official Documents", Linkify: false));
            foreach (var group in ParseDocumentLinks(lines.Skip(documentsAt + 1).ToList()))
            {
                blocks.Add(new RawBlock(BlockKind.Body, 0, "", Structured: group));
            }
        }

        if (actIndex >= 0)
        {
            var actLines = text[(actIndex + ActTextMarker.Length)..].Split('\n').Select(line => line.Trim()).ToList();
            // "Source: <label>" and the PDF URL come first.
            var skip = actLines.FindIndex(line => line.StartsWith("http", StringComparison.OrdinalIgnoreCase));
            blocks.Add(new RawBlock(BlockKind.SubHeading, 0, "Act Text", Linkify: false));
            foreach (var paragraph in Reflow(actLines.Skip(skip + 1)))
            {
                blocks.Add(new RawBlock(NoteRegex().IsMatch(paragraph) ? BlockKind.Note : BlockKind.Body, 0, paragraph));
            }
        }
        return blocks;
    }

    private static List<RawBlock> FormatOpinion(LibraryDocument doc, List<(string, string)> facts)
    {
        var meta = doc.Metadata;
        AddFact(facts, "Court", meta.GetValueOrDefault("court"));
        AddFact(facts, "Docket", doc.Citation);
        AddFact(facts, "Decided", meta.GetValueOrDefault("release_date"));
        AddFact(facts, "Author", meta.GetValueOrDefault("author"));
        AddFact(facts, "Disposition", meta.GetValueOrDefault("disposition"));

        var text = doc.Text.Replace("\r", "");
        // The downloader writes a short header (docket, title, court, dates, sources) before the opinion.
        var bodyStart = text.IndexOf("\n\n", StringComparison.Ordinal);
        var body = bodyStart >= 0 ? text[(bodyStart + 2)..] : text;

        var blocks = new List<RawBlock>();
        var chunks = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var paragraphs = chunks.Length > 3
            ? chunks.Select(chunk => WhitespaceRegex().Replace(chunk, " ").Trim())
            : Reflow(body.Split('\n'));
        foreach (var paragraph in paragraphs.Where(p => p.Length > 0))
        {
            var centered = paragraph.Length < 90 && (paragraph == paragraph.ToUpperInvariant() || CaseCaptionRegex().IsMatch(paragraph));
            blocks.Add(new RawBlock(centered ? BlockKind.DivisionHeading : BlockKind.Body, 0, paragraph));
        }
        return blocks;
    }

    private static int FindHistoryHeader(List<string> lines)
    {
        for (var i = 0; i + 4 < lines.Count; i++)
        {
            if (lines[i] == "Date" && lines[i + 1] == "Chamber" && lines[i + 4] == "Action")
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>History rows arrive one cell per line: date, chamber, up to two journal/page numbers, action.</summary>
    private static List<HistoryRow> ParseHistory(List<string> cells)
    {
        var rows = new List<HistoryRow>();
        var i = 0;
        while (i + 2 < cells.Count)
        {
            if (!HistoryDateRegex().IsMatch(cells[i]) || cells[i + 1] is not ("H" or "S" or "E"))
            {
                i++;
                continue;
            }
            var date = cells[i];
            var chamber = cells[i + 1] switch { "H" => "House", "S" => "Senate", _ => "Exec." };
            i += 2;
            var references = new List<string>();
            while (i < cells.Count && references.Count < 2 && NumberRegex().IsMatch(cells[i]))
            {
                references.Add(cells[i++]);
            }
            if (i >= cells.Count)
            {
                break;
            }
            rows.Add(new HistoryRow(date, chamber, string.Join(" / ", references), cells[i++]));
        }
        return rows;
    }

    /// <summary>"Text / HB10 Act / https://… / Amendments / …": a line before a URL is a link, any other line titles a group.</summary>
    private static List<LinkListModel> ParseDocumentLinks(List<string> lines)
    {
        var groups = new List<LinkListModel>();
        var title = "Documents";
        var links = new List<(string, string)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var isUrl = lines[i].StartsWith("http", StringComparison.OrdinalIgnoreCase);
            if (isUrl)
            {
                continue;
            }
            if (i + 1 < lines.Count && lines[i + 1].StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                links.Add((lines[i], lines[i + 1]));
                continue;
            }
            if (links.Count > 0)
            {
                groups.Add(new LinkListModel(title, links));
                links = [];
            }
            title = lines[i];
        }
        if (links.Count > 0)
        {
            groups.Add(new LinkListModel(title, links));
        }
        return groups;
    }

    /// <summary>Rejoins PDF-extracted lines into paragraphs: a short line ending a sentence closes a paragraph.</summary>
    private static List<string> Reflow(IEnumerable<string> rawLines)
    {
        var lines = rawLines.Select(line => line.Trim()).ToList();
        var lengths = lines.Where(line => line.Length > 0).Select(line => line.Length).Order().ToList();
        var typical = lengths.Count > 0 ? lengths[(int)(lengths.Count * 0.75)] : 80;

        var paragraphs = new List<string>();
        var current = new System.Text.StringBuilder();
        var previousShort = true;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                Flush();
                previousShort = true;
                continue;
            }
            // Headers, captions, signature lines ("HOUSE BILL NO. 10", "AN ACT") and "* * *" stand alone.
            if (IsStandaloneLine(line))
            {
                Flush();
                paragraphs.Add(line);
                previousShort = true;
                continue;
            }
            var startsNew = previousShort || ParagraphStartRegex().IsMatch(line);
            if (startsNew && current.Length > 0 && !char.IsLower(line[0]))
            {
                Flush();
            }
            if (current.Length > 0)
            {
                // Rejoin words hyphenated across a line break.
                if (current[^1] == '-' && char.IsLower(line[0]))
                {
                    current.Length--;
                }
                else
                {
                    current.Append(' ');
                }
            }
            current.Append(line);
            previousShort = line.Length < typical * 0.7 && SentenceEndRegex().IsMatch(line);
        }
        Flush();
        return paragraphs;

        void Flush()
        {
            if (current.Length > 0)
            {
                paragraphs.Add(current.ToString());
                current.Clear();
            }
        }
    }

    private static bool IsStandaloneLine(string line)
        => line == "* * *"
           || (line.Length <= 70 && line.Any(char.IsLetter) && line == line.ToUpperInvariant());

    private static int IndentFor(string line, int previousIndent)
    {
        var marker = MarkerRegex().Match(line);
        if (!marker.Success)
        {
            return previousIndent;
        }
        var token = marker.Groups["token"].Value;
        if (marker.Groups["plain"].Success)
        {
            return 0; // "A."
        }
        if (token.All(char.IsDigit))
        {
            return 1; // "(1)"
        }
        // "(i)" is a sub-item when we are already at the lettered level; otherwise it's the letter i.
        if (RomanRegex().IsMatch(token) && (token.Length > 1 || previousIndent >= 2))
        {
            return 3;
        }
        return char.IsLower(token[0]) ? 2 : 1; // "(a)" / "(A)"
    }

    private static Regex? BuildHighlighter(IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
        {
            return null;
        }
        var alternatives = string.Join("|", terms.OrderByDescending(term => term.Length).Select(Regex.Escape));
        return new Regex($@"\b(?:{alternatives})\w*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static List<Segment> BuildSegments(
        string text,
        IReadOnlyList<CitationSpan> spans,
        IReadOnlyDictionary<string, long> resolved,
        string currentCitation,
        Regex? highlighter)
    {
        var segments = new List<Segment>();
        var position = 0;
        foreach (var span in spans)
        {
            if (!resolved.TryGetValue(span.Key, out var target) || string.Equals(span.Key, currentCitation, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            AddPlain(text[position..span.Start]);
            segments.Add(new Segment(text.Substring(span.Start, span.Length), DocumentId: target));
            position = span.Start + span.Length;
        }
        AddPlain(text[position..]);
        return segments;

        void AddPlain(string value)
        {
            if (value.Length == 0)
            {
                return;
            }
            if (highlighter is null)
            {
                segments.Add(new Segment(value));
                return;
            }
            var last = 0;
            foreach (Match match in highlighter.Matches(value))
            {
                if (match.Index > last)
                {
                    segments.Add(new Segment(value[last..match.Index]));
                }
                segments.Add(new Segment(match.Value, Highlight: true));
                last = match.Index + match.Length;
            }
            if (last < value.Length)
            {
                segments.Add(new Segment(value[last..]));
            }
        }
    }

    private static void AddFact(List<(string, string)> facts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            facts.Add((label, value.Trim()));
        }
    }

    [GeneratedRegex(@"^(?:Art(?:icle|\.)\s*\d|§\s*\d|Rule\s+\d|PREAMBLE$)")]
    private static partial Regex CaptionRegex();

    [GeneratedRegex(@"^(?:Acts?\s+\d{4}|Acts\s+\d{4}|Amended\s+by|Added\s+by|Redesignated|Repealed\s+by|Enacted\s+by|Source:|Comments?\b|Revision\s+Comments?|NOTE:|\{\{NOTE:|HCR\s+No\.|SCR\s+No\.)", RegexOptions.IgnoreCase)]
    private static partial Regex NoteRegex();

    [GeneratedRegex(@"^(?:(?<plain>[A-Z])\.\s|\((?<token>[0-9]+|[a-z]{1,4}|[A-Z])\)\s?)")]
    private static partial Regex MarkerRegex();

    [GeneratedRegex(@"^(?:i|ii|iii|iv|v|vi|vii|viii|ix|x|xi|xii)$")]
    private static partial Regex RomanRegex();

    [GeneratedRegex(@"^\d\d/\d\d(?:/\d{2,4})?$")]
    private static partial Regex HistoryDateRegex();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^(?:Section\s+\d|SECTION\s+\d|§\s*\d|\([0-9a-zA-Z]{1,4}\)\s|[A-Z]\.\s|\d+\.\s)")]
    private static partial Regex ParagraphStartRegex();

    [GeneratedRegex(@"[.:;?!""”)]$")]
    private static partial Regex SentenceEndRegex();

    [GeneratedRegex(@"^(?:v\.|versus|No\.\s)")]
    private static partial Regex CaseCaptionRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
