using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using BayouLex.App.Library;

namespace BayouLex.App.Views;

/// <summary>
/// Selectable text whose citation runs act as hyperlinks. Links are ordinary runs, so they wrap and
/// select like the text around them; clicks are mapped back to a link by hit-testing the layout.
/// </summary>
public sealed class LinkTextBlock : SelectableTextBlock
{
    private readonly List<(int Start, int End, Segment Segment)> _links = [];

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public event EventHandler<LinkActivatedEventArgs>? LinkActivated;

    public LinkTextBlock()
    {
        TextWrapping = TextWrapping.Wrap;
        AddHandler(PointerReleasedEvent, OnReleased, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
    }

    public void SetSegments(IReadOnlyList<Segment> segments, IBrush linkBrush, IBrush hitBrush)
    {
        _links.Clear();
        var inlines = new InlineCollection();
        var position = 0;
        foreach (var segment in segments)
        {
            var run = new Run(segment.Text);
            if (segment.DocumentId is not null || segment.Url is not null)
            {
                run.Foreground = linkBrush;
                run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
                _links.Add((position, position + segment.Text.Length, segment));
            }
            else if (segment.Highlight)
            {
                run.Background = hitBrush;
            }
            inlines.Add(run);
            position += segment.Text.Length;
        }
        Inlines = inlines;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Cursor = _links.Count > 0 && LinkAt(e.GetPosition(this)) is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_links.Count == 0 || e.InitialPressMouseButton is not (MouseButton.Left or MouseButton.Middle))
        {
            return;
        }
        // A drag that selected text is a selection, not a click.
        if (SelectionEnd != SelectionStart)
        {
            return;
        }
        if (LinkAt(e.GetPosition(this)) is { } link)
        {
            var newTab = e.InitialPressMouseButton == MouseButton.Middle || e.KeyModifiers.HasFlag(KeyModifiers.Control);
            LinkActivated?.Invoke(this, new LinkActivatedEventArgs(link, newTab));
            e.Handled = true;
        }
    }

    private Segment? LinkAt(Point point)
    {
        var local = new Point(point.X - Padding.Left, point.Y - Padding.Top);
        var hit = TextLayout.HitTestPoint(local);
        if (!hit.IsInside)
        {
            return null;
        }
        var index = hit.TextPosition;
        foreach (var (start, end, segment) in _links)
        {
            if (index >= start && index < end)
            {
                return segment;
            }
        }
        return null;
    }
}

public sealed class LinkActivatedEventArgs(Segment segment, bool newTab) : EventArgs
{
    public Segment Segment { get; } = segment;
    public bool NewTab { get; } = newTab;
}
