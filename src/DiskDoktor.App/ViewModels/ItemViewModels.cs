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

public sealed partial class FindingViewModel : ObservableObject
{
    private readonly Action _onSelectionChanged;

    public FindingViewModel(Finding finding, Action onSelectionChanged)
    {
        Finding = finding;
        _onSelectionChanged = onSelectionChanged;
        _isSelected = finding.Rule.DefaultSelected;
    }

    public Finding Finding { get; }
    public CleanupRule Rule => Finding.Rule;
    public string Name => Rule.Name;
    public string Description => Rule.Description;
    public string Category => Rule.Category;
    public string Root => Finding.Root;
    public long Size => Finding.TotalSize;
    public string SizeText => SizeFormatter.Format(Size);
    public string CountText => Finding.FileCount == 1 ? "1 dosya" : $"{Finding.FileCount:N0} dosya";
    public RiskLevel Risk => Rule.Risk;
    public string RiskText => RiskText_(Rule.Risk);
    public bool IsSafe => Risk == RiskLevel.Safe;
    public bool IsCaution => Risk == RiskLevel.Caution;
    public bool IsRisky => Risk == RiskLevel.Risky;
    public bool RequiresAdmin => Rule.RequiresAdmin;
    public bool PermanentOnly => Rule.PermanentOnly;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _onSelectionChanged();

    private static string RiskText_(RiskLevel r) => Core.Reports.RiskText.Of(r);
}

public sealed class CategoryViewModel(string name, IReadOnlyList<FindingViewModel> items)
{
    public string Name { get; } = name;
    public IReadOnlyList<FindingViewModel> Items { get; } = items;
    public string TotalText { get; } = SizeFormatter.Format(items.Sum(i => i.Size));
}
