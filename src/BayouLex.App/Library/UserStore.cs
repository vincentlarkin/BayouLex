using Microsoft.Data.Sqlite;

namespace BayouLex.App.Library;

/// <summary>A document reference that survives library rebuilds (row ids do not).</summary>
public sealed record SavedDocument(string Citation, string Title, string Category, DateTimeOffset When);

/// <summary>Bookmarks and reading history, kept in the user's own data directory.</summary>
public sealed class UserStore
{
    private const int HistoryLimit = 300;
    private readonly string _connectionString;

    public UserStore(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS bookmarks (
                citation TEXT NOT NULL,
                category TEXT NOT NULL,
                title TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                PRIMARY KEY (citation, category)
            );
            CREATE TABLE IF NOT EXISTS history (
                id INTEGER PRIMARY KEY,
                citation TEXT NOT NULL,
                category TEXT NOT NULL,
                title TEXT NOT NULL,
                opened_at INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<SavedDocument> GetBookmarks()
        => Query("SELECT citation, title, category, created_at FROM bookmarks ORDER BY created_at DESC");

    public bool IsBookmarked(string citation, string category)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM bookmarks WHERE citation = $citation AND category = $category";
        cmd.Parameters.AddWithValue("$citation", citation);
        cmd.Parameters.AddWithValue("$category", category);
        return cmd.ExecuteScalar() is not null;
    }

    public void AddBookmark(string citation, string title, string category)
        => Execute(
            "INSERT OR REPLACE INTO bookmarks(citation, category, title, created_at) VALUES ($citation, $category, $title, $now)",
            citation, category, title);

    public void RemoveBookmark(string citation, string category)
        => Execute("DELETE FROM bookmarks WHERE citation = $citation AND category = $category", citation, category, "");

    /// <summary>History newest first, one row per document (its latest visit).</summary>
    public IReadOnlyList<SavedDocument> GetHistory()
        => Query("""
            SELECT citation, title, category, MAX(opened_at) AS opened FROM history
            GROUP BY citation, category ORDER BY opened DESC LIMIT 200
            """);

    public void AddHistory(string citation, string title, string category)
    {
        Execute(
            "INSERT INTO history(citation, category, title, opened_at) VALUES ($citation, $category, $title, $now)",
            citation, category, title);
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = $"DELETE FROM history WHERE id NOT IN (SELECT id FROM history ORDER BY id DESC LIMIT {HistoryLimit})";
        cmd.ExecuteNonQuery();
    }

    public void ClearHistory() => Execute("DELETE FROM history", "", "", "");

    private void Execute(string sql, string citation, string category, string title)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$citation", citation);
        cmd.Parameters.AddWithValue("$category", category);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    private List<SavedDocument> Query(string sql)
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        var rows = new List<SavedDocument>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new SavedDocument(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)).ToLocalTime()));
        }
        return rows;
    }

    private SqliteConnection Open()
    {
        var con = new SqliteConnection(_connectionString);
        con.Open();
        return con;
    }
}
