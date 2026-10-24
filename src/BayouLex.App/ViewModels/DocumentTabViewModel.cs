using BayouLex.App.Library;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BayouLex.App.ViewModels;

/// <summary>A place in the library a tab can show: a document, plus the search terms to mark in it.</summary>
public sealed record Visit(long DocumentId, IReadOnlyList<string> Highlight, double ScrollOffset = 0);

/// <summary>One open tab. Like a browser tab, it keeps its own back and forward history.</summary>
public sealed partial class DocumentTabViewModel : ObservableObject
{
    private readonly Stack<Visit> _back = new();
    private readonly Stack<Visit> _forward = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header), nameof(IsWelcome), nameof(ToolTip))]
    private LibraryDocument? _document;

    [ObservableProperty]
    private DocumentLayout? _layout;

    [ObservableProperty]
    private IReadOnlyList<TocNode> _breadcrumb = [];

    [ObservableProperty]
    private IReadOnlyList<string> _highlight = [];

    [ObservableProperty]
    private DocumentSummary? _previous;

    [ObservableProperty]
    private DocumentSummary? _next;

    [ObservableProperty]
    private bool _isBookmarked;

    /// <summary>Where the reader was scrolled, so switching tabs or going back returns to the same spot.</summary>
    public double ScrollOffset { get; set; }

    /// <summary>Set when the reader should scroll to this offset after it next lays out the document.</summary>
    public double? PendingScroll { get; set; }

    public bool IsWelcome => Document is null;

    public string Header => Document is null ? "Welcome" : Citations.Display(Document.Citation);

    public string ToolTip => Document is null ? "BayouLex" : $"{Citations.Display(Document.Citation)} — {Document.Title}";

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public Visit? Current => Document is null ? null : new Visit(Document.Id, Highlight, ScrollOffset);

    /// <summary>Records the current place before moving somewhere new.</summary>
    public void PushHistory()
    {
        if (Current is { } current)
        {
            _back.Push(current);
            _forward.Clear();
        }
    }

    public Visit? TakeBack()
    {
        if (_back.Count == 0)
        {
            return null;
        }
        if (Current is { } current)
        {
            _forward.Push(current);
        }
        return _back.Pop();
    }

    public Visit? TakeForward()
    {
        if (_forward.Count == 0)
        {
            return null;
        }
        if (Current is { } current)
        {
            _back.Push(current);
        }
        return _forward.Pop();
    }
}
