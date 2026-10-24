using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using BayouLex.App.Library;
using BayouLex.App.ViewModels;

namespace BayouLex.App.Views;

public partial class WelcomeView : UserControl
{
    private MainViewModel? _subscribed;

    public WelcomeView()
    {
        InitializeComponent();
    }

    private MainViewModel? Main => DataContext as MainViewModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The welcome page shows library-wide information, not the tab it sits in.
        DataContext = TopLevel.GetTopLevel(this)?.DataContext;
        if (Main is { } main && main != _subscribed)
        {
            _subscribed = main;
            RecentList.ItemsSource = main.History.Take(8).ToList();
            main.History.CollectionChanged += (_, _) => RecentList.ItemsSource = main.History.Take(8).ToList();
        }
    }

    private void SourceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SourceSummary source } && Main?.Library is { } library)
        {
            var node = library.GetChildNodes(null).FirstOrDefault(candidate => candidate.Id == source.NodeId);
            if (node is not null)
            {
                Main.RevealNode(node);
            }
        }
    }

    private void RecentButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SavedDocumentViewModel saved })
        {
            Main?.OpenSaved(saved, newTab: false);
        }
    }
}
