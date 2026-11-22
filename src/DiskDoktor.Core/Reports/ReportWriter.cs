using System.Globalization;
using System.Text;
using DiskDoktor.Core.Cleaning;
using DiskDoktor.Core.Rules;

namespace DiskDoktor.Core.Reports;

public static class SizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (bytes < 0) bytes = 0;
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : v.ToString(v >= 100 ? "0" : "0.#", culture) + " " + Units[u];
    }
}

public static class RiskText
{
    public static string Of(RiskLevel r) => r switch
    {
        RiskLevel.Safe => "Güvenli",
        RiskLevel.Caution => "Dikkat",
        _ => "Riskli",
    };
}

public static class ReportWriter
{
    private static string OutcomeText(ItemOutcome o) => o switch
    {
        ItemOutcome.Deleted => "Silindi",
        ItemOutcome.Trashed => "Çöpe taşındı",
        ItemOutcome.Partial => "Kısmen",
        ItemOutcome.Skipped => "Atlandı",
        _ => "Başarısız",
    };

    public static string ToMarkdown(CleanResult r, string machine, CultureInfo? c = null)
    {
        c ??= CultureInfo.CurrentCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# DiskDoktor temizlik raporu").AppendLine();
        sb.AppendLine($"- Tarih: {r.StartedUtc.ToLocalTime().ToString("g", c)}");
        sb.AppendLine($"- Bilgisayar: {machine}");
        string kip = r.Mode == DeleteMode.Trash ? "Çöp kutusuna taşı" : "Kalıcı sil";
        if (r.Mode == DeleteMode.Trash && r.DeletedPermanentlyCount > 0)
            kip += $" ({r.DeletedPermanentlyCount} kalem kalıcı silindi: yönetici, komut veya çöp kutusu kuralları)";
        sb.AppendLine($"- Kip: {kip}");
        sb.AppendLine($"- {(r.Mode == DeleteMode.Trash ? "Taşınan/kazanılan" : "Kazanılan")} alan: **{SizeFormatter.Format(r.BytesFreed, c)}**");
        sb.AppendLine($"- Başarılı: {r.SucceededCount}, sorunlu: {r.FailedCount}{(r.Cancelled ? " (iptal edildi)" : "")}");
        if (r.LogFile is not null) sb.AppendLine($"- Günlük: `{r.LogFile}`");
        sb.AppendLine();

        foreach (var g in r.Items.GroupBy(i => i.RuleName))
        {
            sb.AppendLine($"## {g.Key} — {SizeFormatter.Format(g.Sum(i => i.BytesFreed), c)}").AppendLine();
            sb.AppendLine("| Durum | Boyut | Yol | Not |").AppendLine("|---|---:|---|---|");
            foreach (var i in g)
                sb.AppendLine($"| {OutcomeText(i.Outcome)} | {SizeFormatter.Format(i.Size, c)} | `{Md(i.Path)}` | {Md(i.Error ?? "")} |");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string ToCsv(CleanResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("kural,yol,boyut_bayt,kazanilan_bayt,durum,hata");
        foreach (var i in r.Items)
            sb.AppendLine(string.Join(',', Csv(i.RuleName), Csv(i.Path),
                i.Size.ToString(CultureInfo.InvariantCulture), i.BytesFreed.ToString(CultureInfo.InvariantCulture),
                Csv(OutcomeText(i.Outcome)), Csv(i.Error ?? "")));
        return sb.ToString();
    }

    private static string Md(string s) => s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    /// <summary>RFC 4180 quoting plus a guard against spreadsheet formula injection.</summary>
    internal static string Csv(string s)
    {
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
