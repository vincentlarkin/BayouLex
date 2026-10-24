using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace BayouLex.App.Library;

public sealed record LibraryInfo(string Path, string DatasetVersion, int DocumentCount, DateTimeOffset? BuiltAt);

public sealed record SourceSummary(long NodeId, string Name, int DocumentCount);

public sealed record TocNode(long Id, long? ParentId, string Category, string Kind, string Label, bool HasChildren);

public sealed record DocumentSummary(long Id, string Citation, string Title, string Category);

public sealed record LibraryDocument(
    long Id,
    string Citation,
    string Title,
    string Category,
    string Bundle,
    string Url,
    string Text,
    int HeadingLines,
    long? TocNodeId,
    string StatusLabel,
    string SessionId,
    string Chamber,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record SearchHit(long Id, string Citation, string Title, string Category, string Snippet);

public sealed record SearchResults(IReadOnlyList<SearchHit> Hits, bool Truncated, TimeSpan Elapsed);

public sealed class LibraryFormatException(string message) : Exception(message);

/// <summary>Read-only access to a BayouLex library (the SQLite file built by build_bayoulex_dataset.py).</summary>
public sealed partial class LibraryDatabase
{
    public const char SnippetStart = '\u0002';
    public const char SnippetEnd = '\u0003';

    private readonly string _connectionString;

    private LibraryDatabase(string path, string connectionString)
    {
        Path = path;
        _connectionString = connectionString;
    }

    public string Path { get; }

    public static LibraryDatabase Open(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Library file not found.", path);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true,
        };
        var library = new LibraryDatabase(path, builder.ToString());
        using var con = library.OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table') AND name IN ('documents', 'toc_nodes', 'documents_fts')";
        var tables = new HashSet<string>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }
        if (!tables.Contains("documents") || !tables.Contains("documents_fts"))
        {
            throw new LibraryFormatException("This file is not a BayouLex library.");
        }
        if (!tables.Contains("toc_nodes"))
        {
            throw new LibraryFormatException("This library was built by an older BayouLex. Rebuild it with scripts/build_bayoulex_dataset.py.");
        }
        return library;
    }

    public LibraryInfo GetInfo()
    {
        using var con = OpenConnection();
        var meta = new Dictionary<string, string>();
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT key, value FROM dataset_metadata";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                meta[reader.GetString(0)] = reader.GetString(1);
            }
        }
        var count = meta.TryGetValue("document_count", out var rawCount) && int.TryParse(rawCount, out var parsed)
            ? parsed
            : Scalar<int>(con, "SELECT COUNT(*) FROM documents");
        DateTimeOffset? builtAt = meta.TryGetValue("built_at_epoch", out var rawEpoch) && long.TryParse(rawEpoch, out var epoch)
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : null;
        return new LibraryInfo(Path, meta.GetValueOrDefault("dataset_version", "unknown"), count, builtAt);
    }

    public IReadOnlyList<SourceSummary> GetSources()
    {
        using var con = OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT n.id, n.label, (SELECT COUNT(*) FROM documents d WHERE d.category = n.category)
            FROM toc_nodes n
            WHERE n.parent_id IS NULL
            ORDER BY n.sort_order
            """;
        var rows = new List<SourceSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new SourceSummary(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
        }
        return rows;
    }

    public IReadOnlyList<TocNode> GetChildNodes(long? parentId)
    {
        using var con = OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT n.id, n.parent_id, n.category, n.kind, n.label,
                   EXISTS (SELECT 1 FROM toc_nodes c WHERE c.parent_id = n.id)
                   OR EXISTS (SELECT 1 FROM documents d WHERE d.toc_node_id = n.id)
            FROM toc_nodes n
            WHERE n.parent_id IS $parent
            ORDER BY n.sort_order
            """;
        cmd.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        var rows = new List<TocNode>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadNode(reader));
        }
        return rows;
    }

    public IReadOnlyList<DocumentSummary> GetDocumentsInNode(long nodeId)
    {
        using var con = OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT id, citation, title, category FROM documents
            WHERE toc_node_id = $node
            ORDER BY sort_order
            """;
        cmd.Parameters.AddWithValue("$node", nodeId);
        return ReadSummaries(cmd);
    }

    /// <summary>The outline nodes from the source down to (and including) the given node.</summary>
    public IReadOnlyList<TocNode> GetNodePath(long nodeId)
    {
        using var con = OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            WITH RECURSIVE chain(id, depth) AS (
                SELECT $node, 0
                UNION ALL
                SELECT n.parent_id, chain.depth + 1 FROM toc_nodes n JOIN chain ON n.id = chain.id
                WHERE n.parent_id IS NOT NULL
            )
            SELECT n.id, n.parent_id, n.category, n.kind, n.label, 1
            FROM chain JOIN toc_nodes n ON n.id = chain.id
            ORDER BY chain.depth DESC
            """;
        cmd.Parameters.AddWithValue("$node", nodeId);
        var rows = new List<TocNode>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadNode(reader));
        }
        return rows;
    }

    public LibraryDocument? GetDocument(long id)
    {
        using var con = OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT id, citation, title, category, bundle, url, text, heading_lines, toc_node_id,
                   status_label, session_id, chamber, metadata_json
            FROM documents WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }
        return new LibraryDocument(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            ParseMetadata(reader.GetString(12)));
    }

    /// <summary>The section before or after this one in reading order, within the same source.</summary>
    public DocumentSummary? GetNeighbor(long id, int direction)
    {
        using var con = OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = direction < 0
            ? """
              SELECT d.id, d.citation, d.title, d.category FROM documents d, documents cur
              WHERE cur.id = $id AND d.category = cur.category AND d.sort_order < cur.sort_order
              ORDER BY d.sort_order DESC LIMIT 1
              """
            : """
              SELECT d.id, d.citation, d.title, d.category FROM documents d, documents cur
              WHERE cur.id = $id AND d.category = cur.category AND d.sort_order > cur.sort_order
              ORDER BY d.sort_order LIMIT 1
              """;
        cmd.Parameters.AddWithValue("$id", id);
        return ReadSummaries(cmd).FirstOrDefault();
    }

    public DocumentSummary? FindByCitation(string citation, string? category = null)
    {
        using var con = OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT id, citation, title, category FROM documents
            WHERE citation = $citation COLLATE NOCASE AND ($category IS NULL OR category = $category)
            ORDER BY sort_order LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$citation", citation);
        cmd.Parameters.AddWithValue("$category", (object?)category ?? DBNull.Value);
        return ReadSummaries(cmd).FirstOrDefault();
    }

    /// <summary>Maps each citation key that exists in the library to its document id.</summary>
    public IReadOnlyDictionary<string, long> ResolveCitations(IEnumerable<string> citations)
    {
        var wanted = citations.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var found = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return found;
        }

        using var con = OpenConnection();
        foreach (var batch in wanted.Chunk(200))
        {
            using var cmd = con.CreateCommand();
            var names = new List<string>();
            for (var i = 0; i < batch.Length; i++)
            {
                names.Add($"$c{i}");
                cmd.Parameters.AddWithValue($"$c{i}", batch[i]);
            }
            cmd.CommandText = $"SELECT citation, MIN(id) FROM documents WHERE citation COLLATE NOCASE IN ({string.Join(",", names)}) GROUP BY citation COLLATE NOCASE";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                found[reader.GetString(0)] = reader.GetInt64(1);
            }
        }
        return found;
    }

    public SearchResults Search(string rawQuery, string? category, int limit, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var ftsQuery = ToFtsQuery(rawQuery);
        if (string.IsNullOrWhiteSpace(ftsQuery))
        {
            return new SearchResults([], false, stopwatch.Elapsed);
        }

        using var con = OpenConnection();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"""
            SELECT d.id, d.citation, d.title, d.category,
                   snippet(documents_fts, 2, '{SnippetStart}', '{SnippetEnd}', ' … ', 18)
            FROM documents_fts
            JOIN documents d ON d.id = documents_fts.rowid
            WHERE documents_fts MATCH $query AND ($category IS NULL OR d.category = $category)
            ORDER BY bm25(documents_fts, 8.0, 4.0, 1.0)
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$query", ftsQuery);
        cmd.Parameters.AddWithValue("$category", (object?)category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", limit + 1);
        using var registration = cancellationToken.Register(() =>
        {
            try { SQLitePCL.raw.sqlite3_interrupt(con.Handle); } catch (Exception) { }
        });

        var hits = new List<SearchHit>();
        try
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                hits.Add(new SearchHit(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? "" : CollapseWhitespace(reader.GetString(4))));
            }
        }
        catch (SqliteException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var truncated = hits.Count > limit;
        if (truncated)
        {
            hits.RemoveAt(hits.Count - 1);
        }
        return new SearchResults(hits, truncated, stopwatch.Elapsed);
    }

    /// <summary>Words to highlight in a document opened from a search (prefix matches, like the search itself).</summary>
    public static IReadOnlyList<string> HighlightTerms(string rawQuery)
        => TokenRegex().Matches(rawQuery)
            .Select(match => match.Value)
            .Where(token => token.Length >= 2 && !OperatorRegex().IsMatch(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string ToFtsQuery(string rawQuery)
    {
        var trimmed = rawQuery.Trim();
        // Let people write real FTS5 syntax ("exact phrase", NEAR, OR, prefix*) when they want it.
        if (trimmed.Any(ch => "\"*():{}".Contains(ch)) || OperatorRegex().IsMatch(trimmed))
        {
            return trimmed;
        }
        return string.Join(" ", TokenRegex()
            .Matches(trimmed)
            .Select(match => match.Value)
            .Where(token => token.Length >= 2)
            .Select(token => token + "*"));
    }

    private static IReadOnlyDictionary<string, string> ParseMetadata(string json)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                // doc_html/doc_text duplicate the body and can be large; nothing reads them here.
                if (property.Name is "doc_html" or "doc_text")
                {
                    continue;
                }
                if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                {
                    values[property.Name] = property.Value.ToString();
                }
            }
        }
        catch (JsonException)
        {
        }
        return values;
    }

    private SqliteConnection OpenConnection()
    {
        var con = new SqliteConnection(_connectionString);
        con.Open();
        return con;
    }

    private static T Scalar<T>(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
    }

    private static TocNode ReadNode(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetBoolean(5));

    private static List<DocumentSummary> ReadSummaries(SqliteCommand cmd)
    {
        var rows = new List<DocumentSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new DocumentSummary(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        return rows;
    }

    private static string CollapseWhitespace(string value) => WhitespaceRegex().Replace(value, " ").Trim();

    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\b(?:AND|OR|NOT|NEAR)\b")]
    private static partial Regex OperatorRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
