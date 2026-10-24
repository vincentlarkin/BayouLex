using BayouLex.App.Library;

namespace BayouLex.App.ViewModels;

/// <summary>A search result row; the snippet is split into plain and matched runs.</summary>
public sealed class SearchHitViewModel(SearchHit hit)
{
    public long DocumentId { get; } = hit.Id;
    public string Citation { get; } = Citations.Display(hit.Citation);
    public string Title { get; } = hit.Title;
    public string Category { get; } = hit.Category;
    public IReadOnlyList<(string Text, bool Match)> Snippet { get; } = SplitSnippet(hit.Snippet);

    private static List<(string, bool)> SplitSnippet(string snippet)
    {
        var parts = new List<(string, bool)>();
        var inMatch = false;
        var start = 0;
        for (var i = 0; i <= snippet.Length; i++)
        {
            var atEnd = i == snippet.Length;
            if (atEnd || snippet[i] == LibraryDatabase.SnippetStart || snippet[i] == LibraryDatabase.SnippetEnd)
            {
                if (i > start)
                {
                    parts.Add((snippet[start..i], inMatch));
                }
                if (!atEnd)
                {
                    inMatch = snippet[i] == LibraryDatabase.SnippetStart;
                }
                start = i + 1;
            }
        }
        return parts;
    }
}

/// <summary>A bookmark or history row.</summary>
public sealed class SavedDocumentViewModel(SavedDocument saved, bool showTime)
{
    public SavedDocument Saved { get; } = saved;
    public string Citation { get; } = Citations.Display(saved.Citation);
    public string Title { get; } = saved.Title;
    public string Detail { get; } = showTime
        ? $"{saved.Category} · {FormatWhen(saved.When)}"
        : saved.Category;

    private static string FormatWhen(DateTimeOffset when)
    {
        var local = when.LocalDateTime;
        if (local.Date == DateTime.Today)
        {
            return local.ToString("h:mm tt");
        }
        return local.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : local.ToString("MMM d, yyyy");
    }
}
