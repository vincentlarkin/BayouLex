using System.Collections.ObjectModel;
using BayouLex.App.Library;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BayouLex.App.ViewModels;

public enum TreeItemKind
{
    Source,
    Bundle,
    Division,
    Document,
    Placeholder,
}

/// <summary>A row in the Contents tree. Children load the first time the row is expanded.</summary>
public sealed partial class TreeItemViewModel : ObservableObject
{
    private readonly LibraryDatabase? _library;
    private bool _loaded;

    private TreeItemViewModel(LibraryDatabase? library, TreeItemKind kind, string label, long? nodeId, long? documentId, bool hasChildren)
    {
        _library = library;
        Kind = kind;
        Label = label;
        NodeId = nodeId;
        DocumentId = documentId;
        if (hasChildren)
        {
            // A stand-in child so the expander shows before the real children are loaded.
            Children.Add(new TreeItemViewModel(null, TreeItemKind.Placeholder, "Loading…", null, null, false));
        }
    }

    public TreeItemKind Kind { get; }
    public string Label { get; }
    public string? Detail { get; private init; }
    public string Tip => Detail is null ? Label : $"{Label}  {Detail}";
    public long? NodeId { get; }
    public long? DocumentId { get; }
    public ObservableCollection<TreeItemViewModel> Children { get; } = [];

    public bool IsDocument => Kind == TreeItemKind.Document;
    public bool IsFolder => Kind is TreeItemKind.Bundle or TreeItemKind.Division;
    public bool IsBook => Kind == TreeItemKind.Source;

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            EnsureLoaded();
        }
    }

    public static TreeItemViewModel FromNode(LibraryDatabase library, TocNode node)
    {
        var kind = node.Kind switch
        {
            "source" => TreeItemKind.Source,
            "bundle" => TreeItemKind.Bundle,
            _ => TreeItemKind.Division,
        };
        return new TreeItemViewModel(library, kind, node.Label, node.Id, null, node.HasChildren);
    }

    public static TreeItemViewModel FromDocument(DocumentSummary doc)
    {
        var label = Citations.ShortLabel(doc.Citation);
        // Bills and opinions carry their own identifiers; codes read better as "§2800  Title".
        return new TreeItemViewModel(null, TreeItemKind.Document, label, null, doc.Id, false)
        {
            Detail = doc.Title,
        };
    }

    public void EnsureLoaded()
    {
        if (_loaded || _library is null || NodeId is null)
        {
            return;
        }
        _loaded = true;
        var items = new List<TreeItemViewModel>();
        // Sections that sit directly in a division always come before its subdivisions.
        items.AddRange(_library.GetDocumentsInNode(NodeId.Value).Select(FromDocument));
        items.AddRange(_library.GetChildNodes(NodeId.Value).Select(node => FromNode(_library, node)));
        Children.Clear();
        foreach (var item in items)
        {
            Children.Add(item);
        }
    }
}
