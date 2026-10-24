using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BayouLex.App.Library;
using BayouLex.App.ViewModels;

namespace BayouLex.App.Views;

public partial class DocumentView : UserControl
{
    public static readonly StyledProperty<double> ReaderFontSizeProperty =
        AvaloniaProperty.Register<DocumentView, double>(nameof(ReaderFontSize), 17);

    private DocumentTabViewModel? _tab;

    public DocumentView()
    {
        InitializeComponent();
        BlocksList.ItemTemplate = new FuncDataTemplate<Block>((block, _) => BuildBlock(block), supportsRecycling: false);
        Scroller.ScrollChanged += (_, _) =>
        {
            if (_tab is not null && _tab.PendingScroll is null)
            {
                _tab.ScrollOffset = Scroller.Offset.Y;
            }
        };
        OnlineButton.Click += (_, _) => Main?.OpenOnlineCommand.Execute(null);
        PreviousButton.Click += (_, _) => Main?.PreviousSectionCommand.Execute(null);
        NextButton.Click += (_, _) => Main?.NextSectionCommand.Execute(null);
    }

    public double ReaderFontSize
    {
        get => GetValue(ReaderFontSizeProperty);
        set => SetValue(ReaderFontSizeProperty, value);
    }

    private MainViewModel? Main => TopLevel.GetTopLevel(this)?.DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_tab is not null)
        {
            _tab.PropertyChanged -= OnTabPropertyChanged;
            _tab.ScrollOffset = Scroller.Offset.Y;
        }
        _tab = DataContext as DocumentTabViewModel;
        if (_tab is not null)
        {
            _tab.PropertyChanged += OnTabPropertyChanged;
            _tab.PendingScroll = _tab.ScrollOffset;
        }
        Render();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ReaderFontSizeProperty && _tab is not null)
        {
            _tab.PendingScroll = Scroller.Offset.Y;
            Render();
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentTabViewModel.Layout))
        {
            Render();
        }
    }

    private void Render()
    {
        BreadcrumbPanel.Children.Clear();
        FactsPanel.Children.Clear();
        if (_tab?.Layout is not { } layout || _tab.Document is not { } doc)
        {
            BlocksList.ItemsSource = null;
            return;
        }

        CitationText.Text = layout.CitationDisplay;
        TitleText.Text = layout.Title;
        RenderBreadcrumb(_tab.Breadcrumb);
        foreach (var (label, value) in layout.Facts)
        {
            FactsPanel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 18, 2),
                Children =
                {
                    new TextBlock { Text = label.ToUpperInvariant() + "  ", FontSize = 10.5, LetterSpacing = 0.6, Foreground = Brush("MutedInkBrush"), VerticalAlignment = VerticalAlignment.Center },
                    new SelectableTextBlock { Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
                },
            });
        }

        OnlineButton.IsVisible = doc.Url.Length > 0;
        OnlineButton.Content = Uri.TryCreate(doc.Url, UriKind.Absolute, out var uri) ? $"View on {uri.Host.Replace("www.", "")} ↗" : "View online ↗";
        SetNeighbor(PreviousButton, _tab.Previous, previous: true);
        SetNeighbor(NextButton, _tab.Next, previous: false);

        BlocksList.ItemsSource = layout.Blocks;
        var target = _tab.PendingScroll ?? 0;
        Dispatcher.UIThread.Post(() =>
        {
            Scroller.Offset = new Vector(0, target);
            if (_tab is not null)
            {
                _tab.PendingScroll = null;
                _tab.ScrollOffset = target;
            }
        }, DispatcherPriority.Loaded);
    }

    private void RenderBreadcrumb(IReadOnlyList<TocNode> path)
    {
        for (var i = 0; i < path.Count; i++)
        {
            var node = path[i];
            if (i > 0)
            {
                BreadcrumbPanel.Children.Add(new TextBlock { Text = "  ›  ", FontSize = 12, Foreground = Brush("MutedInkBrush"), VerticalAlignment = VerticalAlignment.Center });
            }
            var crumb = new Button
            {
                Content = node.Label,
                FontSize = 12,
                Classes = { "link" },
            };
            ToolTip.SetTip(crumb, "Show in Contents");
            crumb.Click += (_, _) => Main?.RevealNode(node);
            BreadcrumbPanel.Children.Add(crumb);
        }
    }

    private static void SetNeighbor(Button button, DocumentSummary? neighbor, bool previous)
    {
        button.IsVisible = neighbor is not null;
        if (neighbor is null)
        {
            return;
        }
        var label = $"{Citations.ShortLabel(neighbor.Citation)}  {neighbor.Title}";
        button.Content = new TextBlock
        {
            Text = previous ? $"◂  {label}" : $"{label}  ▸",
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 12.5,
        };
        ToolTip.SetTip(button, previous ? "Previous section (Alt+Up)" : "Next section (Alt+Down)");
    }

    private Control BuildBlock(Block block)
    {
        var size = ReaderFontSize;
        switch (block)
        {
            case TextBlockModel text:
                return BuildText(text, size);
            case HistoryTableModel table:
                return BuildHistory(table);
            case LinkListModel list:
                return BuildLinks(list);
            default:
                return new TextBlock();
        }
    }

    private Control BuildText(TextBlockModel model, double size)
    {
        var text = new LinkTextBlock
        {
            FontFamily = (FontFamily)Application.Current!.FindResource("ReaderFont")!,
            Foreground = Brush("InkBrush"),
            FontSize = size,
            LineHeight = size * 1.5,
        };
        text.SetSegments(model.Segments, Brush("LinkBrush"), Brush("HitBrush"));
        text.LinkActivated += OnLinkActivated;

        switch (model.Kind)
        {
            case BlockKind.DivisionHeading:
                text.TextAlignment = TextAlignment.Center;
                text.FontSize = size * 0.82;
                text.LineHeight = size * 1.2;
                text.FontWeight = FontWeight.SemiBold;
                text.LetterSpacing = 0.4;
                text.Foreground = Brush("MutedInkBrush");
                text.Margin = new Thickness(0, 0, 0, 6);
                return text;
            case BlockKind.Caption:
                text.FontWeight = FontWeight.SemiBold;
                text.FontSize = size * 1.06;
                text.Margin = new Thickness(0, 12, 0, 12);
                return text;
            case BlockKind.Note:
                text.FontStyle = FontStyle.Italic;
                text.FontSize = size * 0.84;
                text.LineHeight = size * 1.3;
                text.Foreground = Brush("MutedInkBrush");
                return new Border
                {
                    BorderBrush = Brush("GoldBrush"),
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(10, 1, 0, 1),
                    Margin = new Thickness(0, 10, 0, 4),
                    Child = text,
                };
            case BlockKind.SubHeading:
                return new Border
                {
                    BorderBrush = Brush("GoldBrush"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin = new Thickness(0, 22, 0, 10),
                    Padding = new Thickness(0, 0, 0, 3),
                    Child = new TextBlock
                    {
                        Text = string.Concat(model.Segments.Select(segment => segment.Text)).ToUpperInvariant(),
                        FontSize = 12,
                        FontWeight = FontWeight.SemiBold,
                        LetterSpacing = 1.2,
                        Foreground = Brush("BayouBrush"),
                    },
                };
            default:
                text.Margin = new Thickness(30 * model.Indent, 0, 0, size * 0.6);
                return text;
        }
    }

    private Control BuildHistory(HistoryTableModel table)
    {
        var panel = new StackPanel();
        var header = HistoryRowGrid("Date", "Chamber", "Jnl./Pg.", "Action", header: true);
        panel.Children.Add(header);
        var odd = false;
        foreach (var row in table.Rows)
        {
            var grid = HistoryRowGrid(row.Date, row.Chamber, row.Reference, row.Action, header: false);
            panel.Children.Add(new Border { Background = odd ? new SolidColorBrush(Color.Parse("#F5F1E6")) : Brushes.Transparent, Child = grid });
            odd = !odd;
        }
        return new Border
        {
            BorderBrush = Brush("ShadowBrush"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 8),
            Child = panel,
        };
    }

    private Grid HistoryRowGrid(string date, string chamber, string reference, string action, bool header)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("62,70,72,*") };
        if (header)
        {
            grid.Background = Brush("ChromeBrush");
        }
        string[] cells = [date, chamber, reference, action];
        for (var i = 0; i < cells.Length; i++)
        {
            var cell = new SelectableTextBlock
            {
                Text = cells[i],
                FontSize = 12.5,
                FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 4, 6, 4),
            };
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private Control BuildLinks(LinkListModel list)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(new TextBlock { Text = list.Title, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
        foreach (var (label, url) in list.Links)
        {
            var button = new Button { Content = label, Classes = { "link" }, FontSize = 13, Margin = new Thickness(14, 1, 0, 1) };
            ToolTip.SetTip(button, url);
            button.Click += (_, _) => Main?.OpenUrl(url);
            panel.Children.Add(button);
        }
        return panel;
    }

    private void OnLinkActivated(object? sender, LinkActivatedEventArgs e)
    {
        if (e.Segment.DocumentId is { } id)
        {
            Main?.OpenDocument(id, e.NewTab, []);
        }
        else if (e.Segment.Url is { } url)
        {
            Main?.OpenUrl(url);
        }
    }

    private static IBrush Brush(string key) => (IBrush)Application.Current!.FindResource(key)!;
}
