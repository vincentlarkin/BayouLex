using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BayouLex.App.Library;
using BayouLex.App.ViewModels;

namespace BayouLex.App;

public partial class MainWindow : Window, IShell
{
    private bool _revealing;

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel(this);
        DataContext = ViewModel;
        Opened += async (_, _) => await ViewModel.InitializeAsync();

        ContentsTree.SelectionChanged += (_, _) =>
        {
            if (!_revealing && ContentsTree.SelectedItem is TreeItemViewModel item)
            {
                ViewModel.OnTreeItemChosen(item, newTab: false);
            }
        };
        ContentsTree.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && ContentsTree.SelectedItem is TreeItemViewModel item)
            {
                ViewModel.OnTreeItemChosen(item, newTab: e.KeyModifiers.HasFlag(KeyModifiers.Control));
                e.Handled = true;
            }
        };
        SearchResults.SelectionChanged += (_, _) =>
        {
            if (SearchResults.SelectedItem is SearchHitViewModel hit)
            {
                ViewModel.OpenSearchHit(hit, newTab: false);
            }
        };
        BookmarksList.DoubleTapped += (_, _) => OpenSaved(BookmarksList, newTab: false);
        HistoryList.DoubleTapped += (_, _) => OpenSaved(HistoryList, newTab: false);
        BookmarksList.KeyDown += (_, e) => OpenSavedOnEnter(BookmarksList, e);
        HistoryList.KeyDown += (_, e) => OpenSavedOnEnter(HistoryList, e);
        BookmarksList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && BookmarksList.SelectedItem is SavedDocumentViewModel saved)
            {
                ViewModel.RemoveBookmarkCommand.Execute(saved);
            }
        };
    }

    public MainViewModel ViewModel { get; }

    private void OpenSaved(ListBox list, bool newTab)
    {
        if (list.SelectedItem is SavedDocumentViewModel saved)
        {
            ViewModel.OpenSaved(saved, newTab);
        }
    }

    private void OpenSavedOnEnter(ListBox list, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OpenSaved(list, e.KeyModifiers.HasFlag(KeyModifiers.Control));
            e.Handled = true;
        }
    }

    // ----- IShell -----

    public async Task<string?> PickLibraryFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open BayouLex Library",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("BayouLex library") { Patterns = ["*.sqlite", "*.db"] },
                FilePickerFileTypes.All,
            ],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task CopyTextAsync(string text)
    {
        if (Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    public void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            _ = Launcher.LaunchUriAsync(uri);
        }
    }

    public void FocusSearchBox() => Dispatcher.UIThread.Post(() =>
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }, DispatcherPriority.Loaded);

    public void FocusGoToBox()
    {
        GoToBox.Focus();
        GoToBox.SelectAll();
    }

    /// <summary>Selects a row once layout has created it (rows under just-expanded folders don't exist yet).</summary>
    public void RevealTreeItem(TreeItemViewModel item) => Dispatcher.UIThread.Post(() =>
    {
        _revealing = true;
        try
        {
            ContentsTree.SelectedItem = item;
        }
        finally
        {
            _revealing = false;
        }
        // Once layout has measured the newly expanded rows, center the row so its neighbors show too.
        Dispatcher.UIThread.Post(() =>
        {
            if (ContentsTree.TreeContainerFromItem(item) is not Control container)
            {
                return;
            }
            var scroller = ContentsTree.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            var top = scroller?.Content is Visual content ? container.TranslatePoint(new Point(0, 0), content) : null;
            if (scroller is null || top is null)
            {
                container.BringIntoView();
                return;
            }
            var target = top.Value.Y - (scroller.Viewport.Height - container.Bounds.Height) / 2;
            scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(target, 0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));
        }, DispatcherPriority.Background);
    }, DispatcherPriority.Loaded);

    public void CloseWindow() => Close();

    public async Task ShowLibraryManagerAsync(LibraryManagerViewModel manager)
    {
        var window = new Views.LibraryManagerWindow { DataContext = manager };
        var showing = window.ShowDialog(this);
        await manager.LoadAsync();
        await showing;
    }

    public Task ShowAboutAsync()
    {
        var info = ViewModel.LibraryInfo;
        var about = new Window
        {
            Title = "About BayouLex",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Padding = new Thickness(22, 18),
                Child = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 14,
                            Children =
                            {
                                new Image { Width = 56, Height = 56, Source = new Bitmap(AssetLoader.Open(new Uri("avares://bayoulex/Assets/bayoulex-brand.png"))) },
                                new StackPanel
                                {
                                    VerticalAlignment = VerticalAlignment.Center,
                                    Children =
                                    {
                                        new TextBlock { Text = "BayouLex", FontSize = 24, FontWeight = FontWeight.SemiBold, FontFamily = (FontFamily)this.FindResource("ReaderFont")! },
                                        new TextBlock { Text = $"Version {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}", Classes = { "muted" } },
                                    },
                                },
                            },
                        },
                        new TextBlock
                        {
                            TextWrapping = TextWrapping.Wrap,
                            Text = "A desktop library of Louisiana law, built from the official sources published by the Louisiana " +
                                   "Legislature and courts. BayouLex is a research aid; always confirm against the official text.",
                        },
                        new TextBlock
                        {
                            Classes = { "muted" },
                            TextWrapping = TextWrapping.Wrap,
                            Text = info is null
                                ? "No library open."
                                : $"Library {info.DatasetVersion}, {info.DocumentCount:n0} documents\n{info.Path}",
                        },
                        new TextBlock { Classes = { "muted", "small" }, Text = "Type: Source Serif 4 and IBM Plex, SIL Open Font License." },
                    },
                },
            },
        };
        return about.ShowDialog(this);
    }
}
