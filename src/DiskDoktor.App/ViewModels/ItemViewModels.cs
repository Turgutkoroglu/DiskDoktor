using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskDoktor.Core.Platform;
using DiskDoktor.Core.Reports;
using DiskDoktor.Core.Rules;
using DiskDoktor.Core.Scanning;

namespace DiskDoktor.App.ViewModels;

public sealed class DriveViewModel(DriveSnapshot d)
{
    public string Name { get; } = d.Name;
    public string Label { get; } = d.Label;
    public double UsedPercent { get; } = Math.Round(d.UsedPercent, 1);
    public bool IsAlmostFull { get; } = d.UsedPercent >= 90;
    public string FreeText { get; } = $"{SizeFormatter.Format(d.FreeBytes)} boş / {SizeFormatter.Format(d.TotalBytes)}";
}

public sealed partial class ItemViewModel : ObservableObject
{
    private readonly FindingViewModel _owner;

    public ItemViewModel(ScanItem item, FindingViewModel owner, bool selected)
    {
        Item = item;
        _owner = owner;
        _isSelected = selected;
    }

    public ScanItem Item { get; }
    public string Path => Item.Path;
    public string Name => System.IO.Path.GetFileName(Item.Path);
    public string SizeText => SizeFormatter.Format(Item.Size);
    public string? Note => Item.Note;
    public bool HasNote => Item.Note is not null;
    public string DateText => Item.LastWriteUtc == DateTime.MinValue ? "" : Item.LastWriteUtc.ToLocalTime().ToString("d MMM yyyy");

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _owner.OnItemSelectionChanged(value);
}

public sealed partial class FindingViewModel : ObservableObject
{
    private const int MaxShownItems = 500;
    private readonly Action _onSelectionChanged;
    private bool _bulk;

    public FindingViewModel(Finding finding, Action onSelectionChanged)
    {
        Finding = finding;
        _onSelectionChanged = onSelectionChanged;
        _isSelected = finding.Rule.DefaultSelected;
        bool itemsOn = finding.Rule.ItemsSelectedByDefault;
        Items = finding.Items.Select(i => new ItemViewModel(i, this, itemsOn)).ToList();
    }

    public Finding Finding { get; }
    public CleanupRule Rule => Finding.Rule;
    public IReadOnlyList<ItemViewModel> Items { get; }
    public string Name => Rule.Name;
    public string Description => Rule.Description;
    public string Category => Rule.Category;
    public string Root => Finding.IsCommand ? "Komut: " + Rule.CommandText : Finding.Root;
    public string FolderPath => Finding.Root;
    public bool CanOpenFolder => Finding.Root.Length > 0 && Directory.Exists(Finding.Root);
    public bool SizeKnown => Finding.SizeKnown;
    public long Size => Finding.TotalSize;
    public RiskLevel Risk => Rule.Risk;
    public string RiskText => Core.Reports.RiskText.Of(Rule.Risk);
    public bool IsSafe => Risk == RiskLevel.Safe;
    public bool IsCaution => Risk == RiskLevel.Caution;
    public bool IsRisky => Risk == RiskLevel.Risky;
    public bool RequiresAdmin => Rule.RequiresAdmin;
    public bool PermanentOnly => Rule.PermanentOnly || Rule.RequiresAdmin;
    public bool IsCommand => Finding.IsCommand;
    public bool HasItems => Items.Count > 0;
    public bool IsPickList => !Rule.ItemsSelectedByDefault;

    public string SizeText => !SizeKnown ? "Boyut bilinmiyor" : (Finding.AccessDenied ? "en az " : "") + SizeFormatter.Format(Size);

    public string CountText => IsCommand ? (Finding.AccessDenied ? "tam boyut için yönetici gerekli" : "araç tarafından temizlenir")
        : !HasItems ? "içeriği yönetici izniyle görülür"
        : Items.Count == 1 ? $"1 öğe · {Finding.FileCount:N0} dosya" : $"{Items.Count:N0} öğe · {Finding.FileCount:N0} dosya";

    /// <summary>Bytes that will be cleaned: selected items, or the whole estimate for commands / unlisted admin folders.</summary>
    public long SelectedSize => !IsSelected ? 0 : HasItems ? Items.Where(i => i.IsSelected).Sum(i => i.Item.Size) : Size;
    public int SelectedItemCount => Items.Count(i => i.IsSelected);
    public string SelectionText => HasItems && SelectedItemCount != Items.Count
        ? $"{SelectedItemCount:N0}/{Items.Count:N0} seçili · {SizeFormatter.Format(Items.Where(i => i.IsSelected).Sum(i => i.Item.Size))}"
        : "";
    public bool HasPartialSelection => SelectionText.Length > 0;
    public string SelectedSizeText => !SizeKnown && !HasItems ? "Boyut bilinmiyor" : SizeFormatter.Format(SelectedSize);

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownItems), nameof(MoreItemsText), nameof(ExpandText))]
    private bool _isExpanded;

    /// <summary>Only materialised when expanded: some caches have thousands of entries.</summary>
    public IEnumerable<ItemViewModel> ShownItems => IsExpanded ? Items.Take(MaxShownItems) : [];
    public string ExpandText => IsExpanded ? "▾ Öğeleri gizle" : $"▸ {Items.Count:N0} öğeyi göster, tek tek seç";
    public string MoreItemsText => Items.Count > MaxShownItems ? $"… ve {Items.Count - MaxShownItems:N0} öğe daha (en büyükler gösteriliyor)" : "";

    partial void OnIsSelectedChanged(bool value)
    {
        // Ticking a "pick what you want" list with nothing picked means: everything.
        if (value && HasItems && SelectedItemCount == 0) SetAll(true);
        Changed();
    }

    public void SetAll(bool on)
    {
        _bulk = true;
        foreach (var i in Items) i.IsSelected = on;
        _bulk = false;
        Changed();
    }

    internal void OnItemSelectionChanged(bool value)
    {
        if (_bulk) return;
        if (value && !IsSelected) IsSelected = true;           // picking an item selects the finding
        else if (!value && SelectedItemCount == 0 && IsSelected) IsSelected = false;
        else Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(HasPartialSelection));
        OnPropertyChanged(nameof(SelectedSizeText));
        _onSelectionChanged();
    }

    public IReadOnlyList<ScanItem> SelectedItems => Items.Where(i => i.IsSelected).Select(i => i.Item).ToList();
}

public sealed class CategoryViewModel(string name, IReadOnlyList<FindingViewModel> items)
{
    public string Name { get; } = name;
    public IReadOnlyList<FindingViewModel> Items { get; } = items;
    public string TotalText { get; } = SizeFormatter.Format(items.Sum(i => i.Size));
}
