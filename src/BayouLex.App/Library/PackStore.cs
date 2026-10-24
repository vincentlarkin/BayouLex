using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace BayouLex.App.Library;

public sealed record InstalledPack(string Id, string Title, string Group, string Version, int Documents, DateTimeOffset InstalledAt);

/// <summary>
/// The user's library: one SQLite file that installed content packs are merged into.
/// Packs ship without a search index; installing one copies its rows in (renumbered so ids
/// never collide), indexes just those rows, and records the pack so it can be updated or removed.
/// </summary>
public sealed class PackStore
{
    public const int SupportedPackFormat = 1;
    private readonly string _path;
    private readonly string _connectionString;

    public PackStore(string path)
    {
        _path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        EnsureSchema();
    }

    public string Path => _path;

    public IReadOnlyList<InstalledPack> GetInstalled()
    {
        using var con = Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT id, title, group_name, version, documents, installed_at FROM packs ORDER BY rowid";
        var rows = new List<InstalledPack>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new InstalledPack(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt32(4), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5))));
        }
        return rows;
    }

    /// <summary>Installs (or replaces) a pack from a .sqlite.br archive or a plain .sqlite file.</summary>
    public async Task<InstalledPack> InstallAsync(string archivePath, string group, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var packDb = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_path)!, $"installing-{Guid.NewGuid():N}.sqlite");
        try
        {
            if (archivePath.EndsWith(".br", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report("Unpacking…");
                await using var source = File.OpenRead(archivePath);
                await using var brotli = new BrotliStream(source, CompressionMode.Decompress);
                await using var target = File.Create(packDb);
                await brotli.CopyToAsync(target, cancellationToken);
            }
            else
            {
                File.Copy(archivePath, packDb, overwrite: true);
            }

            return await Task.Run(() => Merge(packDb, group, progress, cancellationToken), cancellationToken);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(packDb);
        }
    }

    public void Remove(string packId)
    {
        using var con = Open();
        InTransaction(con, () =>
        {
            RemoveRows(con, packId);
            RefreshMetadata(con);
        });
    }

    private InstalledPack Merge(string packDb, string group, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var con = Open();
        Exec(con, $"ATTACH DATABASE '{packDb.Replace("'", "''")}' AS pack");
        try
        {
            var meta = new Dictionary<string, string>();
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT key, value FROM pack.dataset_metadata";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    meta[reader.GetString(0)] = reader.GetString(1);
                }
            }
            if (!meta.TryGetValue("pack_id", out var packId))
            {
                throw new InvalidDataException("This file is not a BayouLex content pack.");
            }
            if (!int.TryParse(meta.GetValueOrDefault("pack_format"), out var format) || format > SupportedPackFormat)
            {
                throw new InvalidDataException("This pack needs a newer version of BayouLex.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Adding to library…");
            InstalledPack? installed = null;
            InTransaction(con, () => installed = CopyIn(con, packId, meta, group, progress, cancellationToken));
            return installed!;
        }
        finally
        {
            Exec(con, "DETACH DATABASE pack");
        }
    }

    private static InstalledPack CopyIn(
        SqliteConnection con,
        string packId,
        IReadOnlyDictionary<string, string> meta,
        string group,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        RemoveRows(con, packId);

        // Renumber the pack's rows past everything already installed. Source nodes (the tree roots)
        // are shared: a second bills pack hangs its sessions under the existing "Bills" root.
        Exec(con, """
            CREATE TEMP TABLE node_map(old_id INTEGER PRIMARY KEY, new_id INTEGER NOT NULL, shared INTEGER NOT NULL);
            """);
        Exec(con, """
            INSERT INTO node_map(old_id, new_id, shared)
            SELECT p.id, r.id, 1 FROM pack.toc_nodes p
            JOIN main.toc_nodes r ON r.parent_id IS NULL AND p.parent_id IS NULL AND r.category = p.category;
            """);
        Exec(con, """
            INSERT INTO node_map(old_id, new_id, shared)
            SELECT p.id, p.id + (SELECT COALESCE(MAX(id), 0) FROM main.toc_nodes), 0 FROM pack.toc_nodes p
            WHERE p.id NOT IN (SELECT old_id FROM node_map);
            """);
        Exec(con, $"""
            INSERT INTO main.toc_nodes(id, parent_id, category, kind, label, sort_order, pack_id)
            SELECT m.new_id, pm.new_id, p.category, p.kind, p.label, p.sort_order,
                   CASE WHEN p.parent_id IS NULL THEN NULL ELSE '{Sql(packId)}' END
            FROM pack.toc_nodes p
            JOIN node_map m ON m.old_id = p.id AND m.shared = 0
            LEFT JOIN node_map pm ON pm.old_id = p.parent_id;
            """);
        Exec(con, $"""
            INSERT OR IGNORE INTO main.documents(
                id, document_key, doc_id, category, bundle, session_id, chamber, status_group, status_label,
                citation, title, text, url, local_file, metadata_json, source_path, toc_node_id, sort_order,
                heading_lines, pack_id)
            SELECT d.id + (SELECT COALESCE(MAX(id), 0) FROM main.documents), d.document_key, d.doc_id, d.category,
                   d.bundle, d.session_id, d.chamber, d.status_group, d.status_label, d.citation, d.title, d.text,
                   d.url, d.local_file, d.metadata_json, d.source_path, m.new_id, d.sort_order, d.heading_lines,
                   '{Sql(packId)}'
            FROM pack.documents d LEFT JOIN node_map m ON m.old_id = d.toc_node_id;
            """);
        Exec(con, "DROP TABLE temp.node_map;");

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("Indexing for search…");
        Exec(con, $"""
            INSERT INTO documents_fts(rowid, citation, title, text)
            SELECT id, citation, title, text FROM documents WHERE pack_id = '{Sql(packId)}';
            """);

        var documents = Convert.ToInt32(Scalar(con, $"SELECT COUNT(*) FROM documents WHERE pack_id = '{Sql(packId)}'"));
        var installed = new InstalledPack(
            packId,
            meta.GetValueOrDefault("pack_title", packId),
            group,
            meta.GetValueOrDefault("pack_version", ""),
            documents,
            DateTimeOffset.UtcNow);
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO packs(id, title, group_name, version, documents, installed_at)
                VALUES ($id, $title, $group, $version, $documents, $at)
                """;
            cmd.Parameters.AddWithValue("$id", installed.Id);
            cmd.Parameters.AddWithValue("$title", installed.Title);
            cmd.Parameters.AddWithValue("$group", installed.Group);
            cmd.Parameters.AddWithValue("$version", installed.Version);
            cmd.Parameters.AddWithValue("$documents", installed.Documents);
            cmd.Parameters.AddWithValue("$at", installed.InstalledAt.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
        RefreshMetadata(con);
        return installed;
    }

    private static void InTransaction(SqliteConnection con, Action work)
    {
        Exec(con, "BEGIN IMMEDIATE;");
        try
        {
            work();
            Exec(con, "COMMIT;");
        }
        catch
        {
            Exec(con, "ROLLBACK;");
            throw;
        }
    }

    private static void RemoveRows(SqliteConnection con, string packId)
    {
        var id = Sql(packId);
        // External-content FTS needs the old values to delete its entries.
        Exec(con, $"""
            INSERT INTO documents_fts(documents_fts, rowid, citation, title, text)
            SELECT 'delete', id, citation, title, text FROM documents WHERE pack_id = '{id}';
            """);
        Exec(con, $"DELETE FROM documents WHERE pack_id = '{id}';");
        Exec(con, $"DELETE FROM toc_nodes WHERE pack_id = '{id}';");
        // Drop shared source roots no remaining pack uses.
        Exec(con, """
            DELETE FROM toc_nodes WHERE parent_id IS NULL
              AND NOT EXISTS (SELECT 1 FROM toc_nodes c WHERE c.parent_id = toc_nodes.id)
              AND NOT EXISTS (SELECT 1 FROM documents d WHERE d.toc_node_id = toc_nodes.id);
            """);
        Exec(con, $"DELETE FROM packs WHERE id = '{id}';");
    }

    private static void RefreshMetadata(SqliteConnection con)
    {
        // The status bar shows the newest pack's build date as the library version.
        Exec(con, """
            INSERT OR REPLACE INTO dataset_metadata(key, value)
            SELECT 'dataset_version', COALESCE(substr(MAX(version), 1, 8), 'empty') FROM packs;
            INSERT OR REPLACE INTO dataset_metadata(key, value)
            SELECT 'document_count', COUNT(*) FROM documents;
            INSERT OR REPLACE INTO dataset_metadata(key, value)
            VALUES ('built_at_epoch', CAST(strftime('%s','now') AS TEXT));
            """);
    }

    private void EnsureSchema()
    {
        using var con = Open();
        Exec(con, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS dataset_metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS packs (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                group_name TEXT NOT NULL,
                version TEXT NOT NULL,
                documents INTEGER NOT NULL,
                installed_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS documents (
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
                heading_lines INTEGER NOT NULL DEFAULT 0,
                pack_id TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS toc_nodes (
                id INTEGER PRIMARY KEY,
                parent_id INTEGER,
                category TEXT NOT NULL,
                kind TEXT NOT NULL,
                label TEXT NOT NULL,
                sort_order INTEGER NOT NULL,
                pack_id TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_toc_nodes_parent ON toc_nodes(parent_id, sort_order);
            CREATE INDEX IF NOT EXISTS idx_toc_nodes_pack ON toc_nodes(pack_id);
            CREATE INDEX IF NOT EXISTS idx_documents_toc ON documents(toc_node_id, sort_order);
            CREATE INDEX IF NOT EXISTS idx_documents_reading_order ON documents(category, sort_order);
            CREATE INDEX IF NOT EXISTS idx_documents_citation ON documents(citation COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_documents_pack ON documents(pack_id);
            CREATE VIRTUAL TABLE IF NOT EXISTS documents_fts USING fts5(
                citation, title, text,
                content='documents', content_rowid='id',
                tokenize='unicode61', prefix='2 3 4 5'
            );
            """);
    }

    private SqliteConnection Open()
    {
        var con = new SqliteConnection(_connectionString);
        con.Open();
        Exec(con, "PRAGMA busy_timeout = 10000;");
        return con;
    }

    private static void Exec(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    /// <summary>Pack ids come from our own manifest/pack files; still escape them for the SQL literals above.</summary>
    private static string Sql(string value) => value.Replace("'", "''");
}
