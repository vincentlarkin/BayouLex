using System.Collections.ObjectModel;
using BayouLex.App.Library;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BayouLex.App.ViewModels;

public sealed partial class PackItemViewModel : ObservableObject
{
    public PackItemViewModel(PackEntry entry, InstalledPack? installed)
    {
        Entry = entry;
        InstalledVersion = installed?.Version;
        _isChecked = installed is not null || entry.Required;
        RefreshStatus();
    }

    public PackEntry Entry { get; }
    public string Title => Entry.Title;
    public string Detail => $"{FormatBytes(Entry.Bytes)} · {Entry.Documents:n0} documents";
    public bool IsRequired => Entry.Required;
    public bool CanToggle => !Entry.Required;

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _isBusy;

    public string? InstalledVersion { get; private set; }
    public bool IsInstalled => InstalledVersion is not null;
    public bool HasUpdate => IsInstalled && string.CompareOrdinal(Entry.Version, InstalledVersion) > 0;
    public bool NeedsInstall => IsChecked && (!IsInstalled || HasUpdate);
    public bool NeedsRemoval => !IsChecked && IsInstalled && !IsRequired;

    partial void OnIsCheckedChanged(bool value) => RefreshStatus();

    public void MarkInstalled(string version)
    {
        InstalledVersion = version;
        RefreshStatus();
    }

    public void MarkRemoved()
    {
        InstalledVersion = null;
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        Status = (IsInstalled, HasUpdate, IsChecked) switch
        {
            (true, true, true) => "Update available",
            (true, _, false) => "Will be removed",
            (true, _, true) => "Installed",
            (false, _, true) => "Will be installed",
            _ => "",
        };
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(HasUpdate));
    }

    public static string FormatBytes(long bytes)
        => bytes >= 1_000_000 ? $"{bytes / 1_000_000.0:0.0} MB" : $"{Math.Max(1, bytes / 1000):n0} KB";
}

public sealed partial class PackGroupViewModel(string title, string description, bool hasYears) : ObservableObject
{
    public string Title { get; } = title;
    public string Description { get; } = description;
    public bool HasYears { get; } = hasYears;
    public ObservableCollection<PackItemViewModel> Items { get; } = [];

    [RelayCommand]
    private void SelectLatest() => Select(1);

    [RelayCommand]
    private void SelectRecent() => Select(5);

    [RelayCommand]
    private void SelectAll() => Select(int.MaxValue);

    [RelayCommand]
    private void SelectNone() => Select(0);

    /// <summary>Checks the newest <paramref name="count"/> packs (items are listed newest first).</summary>
    private void Select(int count)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].IsChecked = i < count || Items[i].IsRequired;
        }
    }
}

/// <summary>Chooses, installs, updates, and removes content packs. Also the first-run wizard.</summary>
public sealed partial class LibraryManagerViewModel : ObservableObject
{
    private static readonly Dictionary<string, string> GroupDescriptions = new()
    {
        ["Louisiana Law"] = "The core library. Always installed.",
        ["Legislative Bills"] = "One pack per year: every bill and resolution with its history, votes, documents, and enacted act text.",
        ["Supreme Court Decisions"] = "One pack per decade of Louisiana Supreme Court opinions.",
    };

    private readonly PackStore _store;
    private readonly PackCatalog _catalog;
    private CancellationTokenSource? _cts;

    public LibraryManagerViewModel(PackStore store, PackCatalog catalog, bool firstRun)
    {
        _store = store;
        _catalog = catalog;
        IsFirstRun = firstRun;
    }

    public bool IsFirstRun { get; }
    public string Heading => IsFirstRun ? "Welcome to BayouLex" : "Library Manager";
    public string Intro => IsFirstRun
        ? "Louisiana Law is set up and ready. Add any of the collections below now, or later from File › Library Manager."
        : "Choose which collections to keep on this computer. Checked packs are installed or updated; unchecked ones are removed.";

    public ObservableCollection<PackGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _isWorking;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _message = "Checking for available packs…";

    /// <summary>True once anything was installed or removed, so the main window reloads the library.</summary>
    public bool Changed { get; private set; }

    public string SelectionSummary
    {
        get
        {
            var items = Groups.SelectMany(group => group.Items).ToList();
            var download = items.Where(item => item.NeedsInstall).Sum(item => item.Entry.Bytes);
            var removals = items.Count(item => item.NeedsRemoval);
            if (download == 0 && removals == 0)
            {
                return "Nothing to change.";
            }
            var parts = new List<string>();
            if (download > 0)
            {
                parts.Add($"{PackItemViewModel.FormatBytes(download)} to download");
            }
            if (removals > 0)
            {
                parts.Add($"{removals} pack{(removals == 1 ? "" : "s")} to remove");
            }
            return string.Join(", ", parts) + ".";
        }
    }

    public async Task LoadAsync()
    {
        PackManifest? manifest = null;
        try
        {
            manifest = await _catalog.GetManifestAsync(CancellationToken.None);
            Message = "";
        }
        catch (Exception ex)
        {
            manifest = PackCatalog.GetBundledManifest();
            Message = manifest is null
                ? $"Couldn't reach the pack library ({ex.Message}). Check your connection and try again."
                : "Couldn't reach the pack library; showing only what came with BayouLex.";
        }
        if (manifest is null)
        {
            return;
        }

        var installed = _store.GetInstalled().ToDictionary(pack => pack.Id);
        Groups.Clear();
        foreach (var group in manifest.Packs.GroupBy(pack => pack.Group))
        {
            var groupVm = new PackGroupViewModel(
                group.Key,
                GroupDescriptions.GetValueOrDefault(group.Key, ""),
                group.Any(pack => pack.Year is not null));
            foreach (var pack in group.OrderByDescending(pack => pack.Year ?? 0))
            {
                var item = new PackItemViewModel(pack, installed.GetValueOrDefault(pack.Id));
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PackItemViewModel.IsChecked))
                    {
                        OnPropertyChanged(nameof(SelectionSummary));
                    }
                };
                groupVm.Items.Add(item);
            }
            Groups.Add(groupVm);
        }
        OnPropertyChanged(nameof(SelectionSummary));
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var items = Groups.SelectMany(group => group.Items).ToList();
        var work = items.Where(item => item.NeedsInstall || item.NeedsRemoval).ToList();
        if (work.Count == 0)
        {
            Message = "Nothing to change.";
            return;
        }

        IsWorking = true;
        _cts = new CancellationTokenSource();
        var failures = new List<string>();
        try
        {
            for (var i = 0; i < work.Count; i++)
            {
                var item = work[i];
                var step = $"({i + 1} of {work.Count})";
                item.IsBusy = true;
                try
                {
                    if (item.NeedsRemoval)
                    {
                        item.Status = "Removing…";
                        await Task.Run(() => _store.Remove(item.Entry.Id));
                        item.MarkRemoved();
                    }
                    else
                    {
                        await InstallAsync(item, step, _cts.Token);
                    }
                    Changed = true;
                }
                catch (OperationCanceledException)
                {
                    item.RefreshStatus();
                    Message = "Canceled.";
                    return;
                }
                catch (Exception ex)
                {
                    item.Status = "Failed";
                    failures.Add($"{item.Title}: {ex.Message}");
                }
                finally
                {
                    item.IsBusy = false;
                }
                Progress = (i + 1) * 100.0 / work.Count;
            }
            Message = failures.Count == 0 ? "All done." : string.Join("\n", failures);
        }
        finally
        {
            IsWorking = false;
            OnPropertyChanged(nameof(SelectionSummary));
        }
    }

    private bool CanApply() => !IsWorking;

    private async Task InstallAsync(PackItemViewModel item, string step, CancellationToken cancellationToken)
    {
        var bundled = Path.Combine(PackCatalog.BundledDirectory, item.Entry.File);
        string archive;
        var downloaded = false;
        // Use the copy shipped with the app when it is the same version.
        if (File.Exists(bundled) && PackCatalog.GetBundledManifest()?.Packs.FirstOrDefault(p => p.Id == item.Entry.Id)?.Version == item.Entry.Version)
        {
            archive = bundled;
        }
        else
        {
            Message = $"Downloading {item.Title} {step}…";
            var progress = new Progress<double>(value => item.Status = $"Downloading {value:P0}");
            archive = await _catalog.DownloadAsync(item.Entry, progress, cancellationToken);
            downloaded = true;
        }

        try
        {
            Message = $"Installing {item.Title} {step}…";
            var status = new Progress<string>(text => item.Status = text);
            var installed = await _store.InstallAsync(archive, item.Entry.Group, status, cancellationToken);
            item.MarkInstalled(installed.Version);
        }
        finally
        {
            if (downloaded)
            {
                File.Delete(archive);
            }
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();
}
