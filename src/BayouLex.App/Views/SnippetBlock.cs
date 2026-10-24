using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace BayouLex.App.Views;

/// <summary>A search snippet with the matched words in bold.</summary>
public sealed class SnippetBlock : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<(string Text, bool Match)>?> PartsProperty =
        AvaloniaProperty.Register<SnippetBlock, IReadOnlyList<(string Text, bool Match)>?>(nameof(Parts));

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public IReadOnlyList<(string Text, bool Match)>? Parts
    {
        get => GetValue(PartsProperty);
        set => SetValue(PartsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PartsProperty)
        {
            var inlines = new InlineCollection();
            foreach (var (text, match) in Parts ?? [])
            {
                inlines.Add(new Run(text) { FontWeight = match ? FontWeight.Bold : FontWeight.Normal });
            }
            Inlines = inlines;
        }
    }
}
