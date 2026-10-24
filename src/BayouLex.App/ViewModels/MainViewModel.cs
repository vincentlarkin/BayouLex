using System.Collections.ObjectModel;
using BayouLex.App.Library;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BayouLex.App.ViewModels;

/// <summary>What the view model needs from the window (dialogs, clipboard, focus).</summary>
public interface IShell
{
    Task<string?> PickLibraryFileAsync();
    Task CopyTextAsync(string text);
    void OpenUrl(string url);
    void FocusSearchBox();
    void FocusGoToBox();
    void RevealTreeItem(TreeItemViewModel item);
    Task ShowAboutAsync();
    void CloseWindow();
}

public sealed partial class MainViewModel : ObservableObject
{
    public const string AllSources = "All sources";
    private const int SearchLimit = 250;
    private static readonly double[] FontSizes = [13, 14, 15, 16, 17, 18, 20, 22, 24, 28];

    private readonly IShell _shell;
    private readonly AppSettings _settings;
    private readonly UserStore _user;
    private CancellationTokenSource? _searchCts;

    public MainViewModel(IShell shell)
    {
        _shell = shell;
        _settings = AppSettings.Load();
        _user = new UserStore(AppPaths.UserDatabasePath);
        _readerFontSize = _settings.ReaderFontSize;
        Tabs.Add(new DocumentTabViewModel());
        _selectedTab = Tabs[0];
        TryOpenLibrary(_settings.ResolveLibraryPath(), remember: false);
        RefreshSaved();
    }

    public LibraryDatabase? Library { get; private set; }

    public ObservableCollection<TreeItemViewModel> Contents { get; } = [];
    public ObservableCollection<DocumentTabViewModel> Tabs { get; } = [];
    public ObservableCollection<SearchHitViewModel> SearchHits { get; } = [];
    public ObservableCollection<SavedDocumentViewModel> Bookmarks { get; } = [];
    public ObservableCollection<SavedDocumentViewModel> History { get; } = [];
    public ObservableCollection<string> SearchSources { get; } = [AllSources];
    public ObservableCollection<SourceSummary> Sources { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand), nameof(GoForwardCommand), nameof(PreviousSectionCommand),
        nameof(NextSectionCommand), nameof(LocateInContentsCommand), nameof(ToggleBookmarkCommand), nameof(CopyCitationCommand),
        nameof(OpenOnlineCommand))]
    private DocumentTabViewModel _selectedTab;

    [ObservableProperty]
    private int _leftPaneIndex;

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private string _selectedSearchSource = AllSources;

    [ObservableProperty]
    private string _searchStatus = "Type words to find, then press Enter.";

    [ObservableProperty]
    private string _goToText = "";

    [ObservableProperty]
    private double _readerFontSize;

    [ObservableProperty]
    private LibraryInfo? _libraryInfo;

    [ObservableProperty]
    private string? _libraryError;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    public bool HasLibrary => Library is not null;

    public string LibraryStatus => LibraryInfo is { } info
        ? $"Library {info.DatasetVersion} · {info.DocumentCount:n0} documents"
        : "No library";

    public string WindowTitle => SelectedTab.Document is { } doc
        ? $"{Citations.Display(doc.Citation)} — BayouLex"
        : "BayouLex — Louisiana Law Library";

    public string CurrentCitation => SelectedTab.Document is { } doc ? Citations.Formal(doc) : "";

    partial void OnSelectedTabChanged(DocumentTabViewModel value) => RaiseDocumentChanged();

    /// <summary>Someone picked a row in the Contents tree.</summary>
    public void OnTreeItemChosen(TreeItemViewModel item, bool newTab)
    {
        if (item is { IsDocument: true, DocumentId: { } id } && (newTab || SelectedTab.Document?.Id != id))
        {
            OpenDocument(id, newTab, highlight: []);
        }
    }

    partial void OnReaderFontSizeChanged(double value)
    {
        _settings.ReaderFontSize = value;
        SaveSettings();
    }

    partial void OnSelectedSearchSourceChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            _ = SearchAsync();
        }
    }

    // ----- Library -----

    [RelayCommand]
    private async Task OpenLibraryAsync()
    {
        var path = await _shell.PickLibraryFileAsync();
        if (path is not null)
        {
            TryOpenLibrary(path, remember: true);
        }
    }

    public bool TryOpenLibrary(string path, bool remember)
    {
        try
        {
            var library = LibraryDatabase.Open(path);
            Library = library;
            LibraryInfo = library.GetInfo();
            LibraryError = null;
            if (remember)
            {
                _settings.LibraryPath = path;
                SaveSettings();
            }
        }
        catch (Exception ex)
        {
            Library = null;
            LibraryInfo = null;
            LibraryError = ex is FileNotFoundException
                ? $"No library found at {path}"
                : $"Could not open {path}: {ex.Message}";
        }

        Contents.Clear();
        Sources.Clear();
        SearchSources.Clear();
        SearchSources.Add(AllSources);
        SelectedSearchSource = AllSources;
        SearchHits.Clear();
        Tabs.Clear();
        Tabs.Add(new DocumentTabViewModel());
        SelectedTab = Tabs[0];

        if (Library is not null)
        {
            foreach (var node in Library.GetChildNodes(null))
            {
                Contents.Add(TreeItemViewModel.FromNode(Library, node));
            }
            foreach (var source in Library.GetSources())
            {
                Sources.Add(source);
                SearchSources.Add(source.Name);
            }
            StatusMessage = $"Opened {Path.GetFileName(path)}";
        }
        else
        {
            StatusMessage = LibraryError ?? "No library";
        }
        OnPropertyChanged(nameof(HasLibrary));
        OnPropertyChanged(nameof(LibraryStatus));
        return Library is not null;
    }

    // ----- Navigation -----

    public void OpenDocument(long documentId, bool newTab, IReadOnlyList<string> highlight)
    {
        if (Library is null)
        {
            return;
        }
        var tab = SelectedTab;
        if (newTab)
        {
            tab = new DocumentTabViewModel();
            Tabs.Add(tab);
        }
        else
        {
            tab.PushHistory();
        }
        if (Load(tab, new Visit(documentId, highlight)))
        {
            SelectedTab = tab;
        }
        RaiseDocumentChanged();
    }

    private bool Load(DocumentTabViewModel tab, Visit visit)
    {
        var doc = Library?.GetDocument(visit.DocumentId);
        if (doc is null)
        {
            StatusMessage = "That document is not in this library.";
            return false;
        }

        tab.Document = doc;
        tab.Highlight = visit.Highlight;
        tab.Breadcrumb = doc.TocNodeId is { } node ? Library!.GetNodePath(node) : [];
        tab.Previous = Library!.GetNeighbor(doc.Id, -1);
        tab.Next = Library.GetNeighbor(doc.Id, +1);
        tab.IsBookmarked = _user.IsBookmarked(doc.Citation, doc.Category);
        tab.ScrollOffset = visit.ScrollOffset;
        tab.PendingScroll = visit.ScrollOffset;
        tab.Layout = DocumentFormatter.Format(doc, Library, visit.Highlight);

        _user.AddHistory(doc.Citation, doc.Title, doc.Category);
        RefreshHistory();
        StatusMessage = visit.Highlight.Count > 0 ? $"Search terms highlighted: {string.Join(", ", visit.Highlight)}" : "Ready";
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack()
    {
        if (SelectedTab.TakeBack() is { } visit)
        {
            Load(SelectedTab, visit);
            RaiseDocumentChanged();
        }
    }

    private bool CanGoBack() => SelectedTab.CanGoBack;

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoForward()
    {
        if (SelectedTab.TakeForward() is { } visit)
        {
            Load(SelectedTab, visit);
            RaiseDocumentChanged();
        }
    }

    private bool CanGoForward() => SelectedTab.CanGoForward;

    [RelayCommand(CanExecute = nameof(HasPrevious))]
    private void PreviousSection()
    {
        if (SelectedTab.Previous is { } previous)
        {
            OpenDocument(previous.Id, newTab: false, highlight: []);
        }
    }

    private bool HasPrevious() => SelectedTab.Previous is not null;

    [RelayCommand(CanExecute = nameof(HasNext))]
    private void NextSection()
    {
        if (SelectedTab.Next is { } next)
        {
            OpenDocument(next.Id, newTab: false, highlight: []);
        }
    }

    private bool HasNext() => SelectedTab.Next is not null;

    [RelayCommand]
    private void NewTab()
    {
        var tab = new DocumentTabViewModel();
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    [RelayCommand]
    private void CloseTab(DocumentTabViewModel? tab)
    {
        tab ??= SelectedTab;
        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }
        Tabs.RemoveAt(index);
        if (Tabs.Count == 0)
        {
            Tabs.Add(new DocumentTabViewModel());
        }
        SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    [RelayCommand]
    private void Exit() => _shell.CloseWindow();

    /// <summary>Expands the Contents tree down to the open document and selects it.</summary>
    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void LocateInContents()
    {
        if (SelectedTab.Document is not { } doc || SelectedTab.Breadcrumb.Count == 0)
        {
            return;
        }
        var division = ExpandPath(SelectedTab.Breadcrumb);
        var target = division?.Children.FirstOrDefault(candidate => candidate.DocumentId == doc.Id) ?? division;
        if (target is not null)
        {
            _shell.RevealTreeItem(target);
        }
    }

    /// <summary>Shows an outline division (from a breadcrumb or the welcome page) in the Contents tree.</summary>
    public void RevealNode(TocNode node)
    {
        if (Library is not null && ExpandPath(Library.GetNodePath(node.Id)) is { } item)
        {
            item.IsExpanded = true;
            _shell.RevealTreeItem(item);
        }
    }

    private TreeItemViewModel? ExpandPath(IReadOnlyList<TocNode> path)
    {
        LeftPaneIndex = 0;
        var level = Contents;
        TreeItemViewModel? item = null;
        foreach (var node in path)
        {
            var next = level.FirstOrDefault(candidate => candidate.NodeId == node.Id);
            if (next is null)
            {
                break;
            }
            item = next;
            item.IsExpanded = true;
            level = item.Children;
        }
        return item;
    }

    // ----- Search and go-to -----

    [RelayCommand]
    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        SearchHits.Clear();
        var query = SearchQuery.Trim();
        if (Library is null || query.Length < 2)
        {
            SearchStatus = Library is null ? "Open a library to search." : "Type at least two letters.";
            return;
        }

        var cts = _searchCts = new CancellationTokenSource();
        var category = SelectedSearchSource == AllSources ? null : SelectedSearchSource;
        SearchStatus = "Searching…";
        try
        {
            var library = Library;
            var results = await Task.Run(() => library.Search(query, category, SearchLimit, cts.Token), cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }
            foreach (var hit in results.Hits)
            {
                SearchHits.Add(new SearchHitViewModel(hit));
            }
            var count = results.Truncated ? $"First {results.Hits.Count:n0}" : $"{results.Hits.Count:n0}";
            SearchStatus = results.Hits.Count == 0
                ? "No documents matched."
                : $"{count} match{(results.Hits.Count == 1 ? "" : "es")} · {results.Elapsed.TotalMilliseconds:n0} ms";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SearchStatus = $"Search failed: {ex.Message}";
        }
    }

    public void OpenSearchHit(SearchHitViewModel hit, bool newTab)
        => OpenDocument(hit.DocumentId, newTab, LibraryDatabase.HighlightTerms(SearchQuery));

    /// <summary>The toolbar box: a citation jumps straight there, anything else becomes a search.</summary>
    [RelayCommand]
    private async Task GoToAsync()
    {
        var text = GoToText.Trim();
        if (Library is null || text.Length == 0)
        {
            return;
        }
        foreach (var key in Citations.ParseInput(text, SelectedTab.Document?.Citation))
        {
            if (Library.FindByCitation(key) is { } doc)
            {
                OpenDocument(doc.Id, newTab: false, highlight: []);
                StatusMessage = $"Went to {Citations.Display(doc.Citation)}";
                return;
            }
        }
        SearchQuery = text;
        LeftPaneIndex = 1;
        await SearchAsync();
        StatusMessage = $"No citation \"{text}\"; showing search results instead.";
    }

    [RelayCommand]
    private void FocusSearch()
    {
        LeftPaneIndex = 1;
        _shell.FocusSearchBox();
    }

    [RelayCommand]
    private void FocusGoTo() => _shell.FocusGoToBox();

    [RelayCommand]
    private void ShowPane(string index) => LeftPaneIndex = int.Parse(index);

    // ----- Bookmarks and history -----

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void ToggleBookmark()
    {
        if (SelectedTab.Document is not { } doc)
        {
            return;
        }
        if (SelectedTab.IsBookmarked)
        {
            _user.RemoveBookmark(doc.Citation, doc.Category);
            StatusMessage = $"Removed bookmark for {Citations.Display(doc.Citation)}";
        }
        else
        {
            _user.AddBookmark(doc.Citation, doc.Title, doc.Category);
            StatusMessage = $"Bookmarked {Citations.Display(doc.Citation)}";
        }
        SelectedTab.IsBookmarked = !SelectedTab.IsBookmarked;
        RefreshBookmarks();
    }

    public void OpenSaved(SavedDocumentViewModel item, bool newTab)
    {
        if (Library?.FindByCitation(item.Saved.Citation, item.Saved.Category) is { } doc)
        {
            OpenDocument(doc.Id, newTab, []);
        }
        else
        {
            StatusMessage = $"{item.Citation} is not in this library.";
        }
    }

    [RelayCommand]
    private void RemoveBookmark(SavedDocumentViewModel? item)
    {
        if (item is null)
        {
            return;
        }
        _user.RemoveBookmark(item.Saved.Citation, item.Saved.Category);
        if (SelectedTab.Document is { } doc && doc.Citation == item.Saved.Citation && doc.Category == item.Saved.Category)
        {
            SelectedTab.IsBookmarked = false;
        }
        RefreshBookmarks();
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _user.ClearHistory();
        RefreshHistory();
    }

    // ----- Reading -----

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task CopyCitationAsync()
    {
        if (SelectedTab.Document is { } doc)
        {
            var citation = Citations.Formal(doc);
            await _shell.CopyTextAsync(citation);
            StatusMessage = $"Copied \"{citation}\"";
        }
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void OpenOnline()
    {
        if (SelectedTab.Document is { Url.Length: > 0 } doc)
        {
            _shell.OpenUrl(doc.Url);
        }
    }

    [RelayCommand]
    private void LargerText() => ReaderFontSize = FontSizes.FirstOrDefault(size => size > ReaderFontSize, FontSizes[^1]);

    [RelayCommand]
    private void SmallerText() => ReaderFontSize = FontSizes.LastOrDefault(size => size < ReaderFontSize, FontSizes[0]);

    [RelayCommand]
    private void ResetText() => ReaderFontSize = 17;

    [RelayCommand]
    private Task AboutAsync() => _shell.ShowAboutAsync();

    public void OpenUrl(string url) => _shell.OpenUrl(url);

    private bool HasDocument() => SelectedTab.Document is not null;

    private void RaiseDocumentChanged()
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(CurrentCitation));
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
        PreviousSectionCommand.NotifyCanExecuteChanged();
        NextSectionCommand.NotifyCanExecuteChanged();
        LocateInContentsCommand.NotifyCanExecuteChanged();
        ToggleBookmarkCommand.NotifyCanExecuteChanged();
        CopyCitationCommand.NotifyCanExecuteChanged();
        OpenOnlineCommand.NotifyCanExecuteChanged();
    }

    private void RefreshSaved()
    {
        RefreshBookmarks();
        RefreshHistory();
    }

    private void RefreshBookmarks()
    {
        Bookmarks.Clear();
        foreach (var saved in _user.GetBookmarks())
        {
            Bookmarks.Add(new SavedDocumentViewModel(saved, showTime: false));
        }
    }

    private void RefreshHistory()
    {
        History.Clear();
        foreach (var saved in _user.GetHistory())
        {
            History.Add(new SavedDocumentViewModel(saved, showTime: true));
        }
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (IOException)
        {
            StatusMessage = "Could not save settings.";
        }
    }
}
