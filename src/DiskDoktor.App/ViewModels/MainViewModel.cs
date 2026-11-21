using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskDoktor.App.Services;
using DiskDoktor.Core.Cleaning;
using DiskDoktor.Core.Platform;
using DiskDoktor.Core.Reports;
using DiskDoktor.Core.Rules;
using DiskDoktor.Core.Scanning;

namespace DiskDoktor.App.ViewModels;

public enum Screen { Start, Scanning, Results, Confirm, Cleaning, Done }

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IScanner _scanner;
    private readonly ICleaner _cleaner;
    private readonly IDriveService _drives;
    private readonly IShell _shell;
    private readonly IReadOnlyList<CleanupRule> _rules;
    private readonly List<FindingViewModel> _all = [];
    private CancellationTokenSource? _cts;

    public MainViewModel(IScanner scanner, ICleaner cleaner, IDriveService drives, IShell shell,
        IReadOnlyList<CleanupRule> rules, string? warning, OsKind os)
    {
        _scanner = scanner;
        _cleaner = cleaner;
        _drives = drives;
        _shell = shell;
        _rules = rules;
        Warning = warning;
        OsName = os switch { OsKind.Windows => "Windows", OsKind.MacOS => "macOS", _ => "Linux" };
        RuleCountText = $"{OsName} için {rules.Count} temizlik kuralı";
        RefreshDrives();
    }

    public string OsName { get; }
    public string RuleCountText { get; }
    public string? Warning { get; }
    public bool HasWarning => Warning is not null;

    public ObservableCollection<DriveViewModel> Drives { get; } = [];
    public ObservableCollection<CategoryViewModel> Categories { get; } = [];
    public ObservableCollection<FindingViewModel> ConfirmItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStart), nameof(IsScanning), nameof(IsResults), nameof(IsConfirm),
        nameof(IsCleaning), nameof(IsDone), nameof(IsOverlay), nameof(IsBusy))]
    private Screen _screen = Screen.Start;

    public bool IsStart => Screen == Screen.Start;
    public bool IsScanning => Screen == Screen.Scanning;
    public bool IsResults => Screen is Screen.Results or Screen.Confirm or Screen.Cleaning or Screen.Done;
    public bool IsConfirm => Screen == Screen.Confirm;
    public bool IsCleaning => Screen == Screen.Cleaning;
    public bool IsDone => Screen == Screen.Done;
    public bool IsOverlay => Screen is Screen.Confirm or Screen.Cleaning or Screen.Done;
    public bool IsBusy => Screen is Screen.Scanning or Screen.Cleaning;

    [ObservableProperty] private string _statusText = "Taramaya hazır.";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _showSafe = true;
    [ObservableProperty] private bool _showCaution = true;
    [ObservableProperty] private bool _showRisky = true;
    [ObservableProperty] private bool _useTrash = true;

    [ObservableProperty] private string _safeTotalText = "0 B";
    [ObservableProperty] private string _cautionTotalText = "0 B";
    [ObservableProperty] private string _riskyTotalText = "0 B";
    [ObservableProperty] private string _foundTotalText = "0 B";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestCleanCommand))]
    private int _selectedCount;
    [ObservableProperty] private string _selectedTotalText = "0 B";
    [ObservableProperty] private bool _hasRiskySelected;
    [ObservableProperty] private bool _hasPermanentOnlySelected;
    [ObservableProperty] private bool _isEmptyResult;

    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _progressText = "";

    [ObservableProperty] private CleanResult? _lastResult;
    [ObservableProperty] private string _resultTitle = "";
    [ObservableProperty] private string _resultDetail = "";
    [ObservableProperty] private bool _resultHasProblems;
    [ObservableProperty] private string _problemText = "";

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnShowSafeChanged(bool value) => ApplyFilter();
    partial void OnShowCautionChanged(bool value) => ApplyFilter();
    partial void OnShowRiskyChanged(bool value) => ApplyFilter();

    public IReadOnlyList<FindingViewModel> AllFindings => _all;

    private void RefreshDrives()
    {
        Drives.Clear();
        try
        {
            foreach (var d in _drives.GetDrives()) Drives.Add(new DriveViewModel(d));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "Disk bilgisi okunamadı: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy) return;
        _cts = new CancellationTokenSource();
        Screen = Screen.Scanning;
        ProgressValue = 0;
        ProgressText = "Tarama başlıyor…";
        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressValue = 100.0 * p.RuleIndex / Math.Max(1, p.RuleCount);
            ProgressText = $"Taranıyor: {p.RuleName}";
        });
        try
        {
            var result = await _scanner.ScanAsync(_rules, progress, _cts.Token);
            LoadFindings(result);
            Screen = Screen.Results;
            StatusText = $"{result.Findings.Count} bulgu, {SizeFormatter.Format(result.TotalSize)} temizlenebilir " +
                         $"({result.Duration.TotalSeconds:0.0} sn).";
        }
        catch (OperationCanceledException)
        {
            Screen = _all.Count > 0 ? Screen.Results : Screen.Start;
            StatusText = "Tarama iptal edildi.";
        }
        catch (Exception ex)
        {
            Screen = Screen.Start;
            StatusText = "Tarama başarısız: " + ex.Message;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    internal void LoadFindings(ScanResult result)
    {
        _all.Clear();
        foreach (var f in result.Findings.OrderByDescending(f => f.TotalSize))
            _all.Add(new FindingViewModel(f, UpdateSelection));
        LoadFindingsFromCurrent();
    }

    private void ApplyFilter()
    {
        var q = SearchText.Trim();
        var visible = _all.Where(f =>
            (f.IsSafe && ShowSafe || f.IsCaution && ShowCaution || f.IsRisky && ShowRisky) &&
            (q.Length == 0 ||
             f.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
             f.Root.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
             f.Category.Contains(q, StringComparison.CurrentCultureIgnoreCase)));

        Categories.Clear();
        foreach (var g in visible.GroupBy(f => f.Category)
                     .Select(g => new CategoryViewModel(g.Key, g.ToList()))
                     .OrderBy(c => c.Items.Min(i => i.Risk))            // safest groups first
                     .ThenByDescending(c => c.Items.Sum(i => i.Size)))
            Categories.Add(g);
    }

    private void UpdateSelection()
    {
        var sel = _all.Where(f => f.IsSelected).ToList();
        SelectedCount = sel.Count;
        SelectedTotalText = SizeFormatter.Format(sel.Sum(f => f.Size));
        HasRiskySelected = sel.Any(f => f.IsRisky);
        HasPermanentOnlySelected = sel.Any(f => f.PermanentOnly);
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        foreach (var f in Categories.SelectMany(c => c.Items)) f.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var f in _all) f.IsSelected = false;
    }

    [RelayCommand]
    private void OpenFolder(FindingViewModel? f)
    {
        if (f is not null) _shell.OpenFolder(f.Root);
    }

    private bool CanRequestClean() => SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanRequestClean))]
    private void RequestClean()
    {
        ConfirmItems.Clear();
        foreach (var f in _all.Where(f => f.IsSelected)) ConfirmItems.Add(f);
        if (ConfirmItems.Count == 0) return;
        Screen = Screen.Confirm;
    }

    [RelayCommand]
    private void Back()
    {
        if (Screen is Screen.Confirm or Screen.Done) Screen = Screen.Results;
    }

    [RelayCommand]
    private async Task ConfirmCleanAsync()
    {
        if (Screen != Screen.Confirm) return;
        _cts = new CancellationTokenSource();
        Screen = Screen.Cleaning;
        ProgressValue = 0;
        ProgressText = "Temizlik başlıyor…";
        var requests = ConfirmItems.Select(f => new CleanRequest(f.Finding, f.Finding.Items)).ToList();
        var progress = new Progress<CleanProgress>(p =>
        {
            ProgressValue = 100.0 * p.Done / Math.Max(1, p.Total);
            ProgressText = $"{p.Done}/{p.Total} · {SizeFormatter.Format(p.BytesFreed)} · {Shorten(p.CurrentPath)}";
        });
        try
        {
            var result = await _cleaner.CleanAsync(requests, UseTrash ? DeleteMode.Trash : DeleteMode.Permanent, progress, _cts.Token);
            ShowResult(result);
        }
        catch (Exception ex)
        {
            ResultTitle = "Temizlik tamamlanamadı";
            ResultDetail = ex.Message;
            ResultHasProblems = true;
            ProblemText = "";
            Screen = Screen.Done;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    private void ShowResult(CleanResult r)
    {
        LastResult = r;
        bool trash = r.Mode == DeleteMode.Trash && r.Items.Any(i => i.Outcome == ItemOutcome.Trashed);
        ResultTitle = (r.Cancelled ? "İptal edildi · " : "") + SizeFormatter.Format(r.BytesFreed) +
                      (trash ? " temizlendi (bir kısmı çöp kutusunda)" : " kazanıldı");
        ResultDetail = $"{r.SucceededCount} öğe temizlendi, {r.FailedCount} öğe sorunlu. Süre {r.Duration.TotalSeconds:0.0} sn." +
                       (trash ? " Çöp kutusu boşaltılınca alan tamamen açılır." : "");
        var problems = r.Items.Where(i => i.Error is not null).Take(6).ToList();
        ResultHasProblems = problems.Count > 0;
        ProblemText = string.Join("\n", problems.Select(p => $"• {Shorten(p.Path)} — {p.Error}"));
        Screen = Screen.Done;
        RefreshDrives();
        StatusText = ResultTitle;

        // Drop what was cleaned from the list; a fresh scan shows what is left.
        var cleaned = r.Items.Where(i => i.Outcome is ItemOutcome.Deleted or ItemOutcome.Trashed)
                              .Select(i => i.Path).ToHashSet();
        foreach (var f in ConfirmItems.ToList())
            if (f.Finding.Items.All(i => cleaned.Contains(i.Path))) _all.Remove(f);
        LoadFindingsFromCurrent();
    }

    private void LoadFindingsFromCurrent()
    {
        SafeTotalText = SizeFormatter.Format(_all.Where(f => f.IsSafe).Sum(f => f.Size));
        CautionTotalText = SizeFormatter.Format(_all.Where(f => f.IsCaution).Sum(f => f.Size));
        RiskyTotalText = SizeFormatter.Format(_all.Where(f => f.IsRisky).Sum(f => f.Size));
        FoundTotalText = SizeFormatter.Format(_all.Sum(f => f.Size));
        IsEmptyResult = _all.Count == 0;
        ApplyFilter();
        UpdateSelection();
    }

    [RelayCommand]
    private async Task ExportMarkdownAsync()
    {
        if (LastResult is null) return;
        var path = await _shell.SaveTextAsync($"diskdoktor-rapor-{DateTime.Now:yyyyMMdd-HHmm}", "md", "Markdown",
            ReportWriter.ToMarkdown(LastResult, _shell.MachineName));
        if (path is not null) StatusText = "Rapor kaydedildi: " + path;
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (LastResult is null) return;
        var path = await _shell.SaveTextAsync($"diskdoktor-rapor-{DateTime.Now:yyyyMMdd-HHmm}", "csv", "CSV",
            ReportWriter.ToCsv(LastResult));
        if (path is not null) StatusText = "Rapor kaydedildi: " + path;
    }

    private static string Shorten(string path) => path.Length <= 70 ? path : "…" + path[^69..];
}
