using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BayouLex.App.Library;

/// <summary>One downloadable pack, as listed in packs.json.</summary>
public sealed record PackEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("documents")] int Documents,
    [property: JsonPropertyName("year")] int? Year = null,
    [property: JsonPropertyName("required")] bool Required = false);

public sealed record PackManifest(
    [property: JsonPropertyName("format")] int Format,
    [property: JsonPropertyName("generated_at")] string GeneratedAt,
    [property: JsonPropertyName("packs")] IReadOnlyList<PackEntry> Packs);

/// <summary>
/// Where packs come from: the published GitHub release by default, a local folder
/// (BAYOULEX_PACKS=/path/to/packs) for development, and the packs folder shipped next to the app.
/// </summary>
public sealed class PackCatalog
{
    public const string DefaultSource = "https://github.com/vincentlarkin/BayouLex/releases/download/library";
    private static readonly HttpClient Http = CreateHttp();

    public PackCatalog(string? source = null)
    {
        Source = source
                 ?? Environment.GetEnvironmentVariable("BAYOULEX_PACKS")
                 ?? DefaultSource;
    }

    public string Source { get; }

    private bool IsLocal => !Source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                            && !Source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder of packs shipped beside the executable, if any.</summary>
    public static string BundledDirectory => Path.Combine(AppContext.BaseDirectory, "packs");

    public async Task<PackManifest> GetManifestAsync(CancellationToken cancellationToken)
    {
        var json = IsLocal
            ? await File.ReadAllTextAsync(Path.Combine(Source, "packs.json"), cancellationToken)
            : await Http.GetStringAsync($"{Source.TrimEnd('/')}/packs.json", cancellationToken);
        return JsonSerializer.Deserialize<PackManifest>(json) ?? throw new InvalidDataException("Empty pack manifest.");
    }

    public static PackManifest? GetBundledManifest()
    {
        var path = Path.Combine(BundledDirectory, "packs.json");
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(path)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Fetches a pack to a temporary file and verifies its checksum. The caller deletes the file.</summary>
    public async Task<string> DownloadAsync(PackEntry pack, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var target = Path.Combine(Path.GetTempPath(), $"bayoulex-{pack.Id}-{Guid.NewGuid():N}.sqlite.br");
        try
        {
            if (IsLocal)
            {
                File.Copy(Path.Combine(Source, pack.File), target, overwrite: true);
                progress?.Report(1);
            }
            else
            {
                var url = pack.Url ?? $"{Source.TrimEnd('/')}/{pack.File}";
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? pack.Bytes;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var file = File.Create(target);
                var buffer = new byte[1 << 16];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    progress?.Report(total > 0 ? (double)received / total : 0);
                }
            }

            await using (var check = File.OpenRead(target))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(check, cancellationToken));
                if (!actual.Equals(pack.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"{pack.Title} did not download correctly (checksum mismatch). Try again.");
                }
            }
            return target;
        }
        catch
        {
            File.Delete(target);
            throw;
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"BayouLex/{typeof(PackCatalog).Assembly.GetName().Version?.ToString(3)}");
        return http;
    }
}
