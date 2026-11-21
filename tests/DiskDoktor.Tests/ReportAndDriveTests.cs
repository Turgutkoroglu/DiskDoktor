using System.Globalization;
using DiskDoktor.Core.Cleaning;
using DiskDoktor.Core.Platform;
using DiskDoktor.Core.Reports;
using Xunit;

namespace DiskDoktor.Tests;

public class ReportTests
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1,5 KB")]
    [InlineData(1073741824L * 250, "250 GB")]
    public void SizeFormatter_UsesCulture(long bytes, string expected) =>
        Assert.Equal(expected, SizeFormatter.Format(bytes, Tr));

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=cmd|' /C calc'!A0", "'=cmd|' /C calc'!A0")]
    [InlineData("-2+3", "'-2+3")]
    public void Csv_QuotesAndBlocksFormulaInjection(string input, string expected) =>
        Assert.Equal(expected, ReportWriter.Csv(input));

    [Fact]
    public void Markdown_ContainsTotalsAndEscapesPipes()
    {
        var r = new CleanResult
        {
            Mode = DeleteMode.Permanent, StartedUtc = DateTime.UtcNow, Duration = TimeSpan.FromSeconds(1),
            Items =
            [
                new("/a|b", "Önbellek", 2048, 2048, ItemOutcome.Deleted, null),
                new("/c", "Önbellek", 10, 0, ItemOutcome.Failed, "izin yok"),
            ],
        };
        var md = ReportWriter.ToMarkdown(r, "pc", Tr);
        Assert.Contains("**2 KB**", md);
        Assert.Contains("/a\\|b", md);
        Assert.Contains("izin yok", md);
        Assert.Equal(3, ReportWriter.ToCsv(r).Trim().Split('\n').Length);
    }
}

public class DriveFilterTests
{
    [Fact]
    public void Linux_HidesPseudoAndDuplicateMounts()
    {
        const long G = 1L << 30;
        var drives = DriveService.Filter(
        [
            ("/", "ext4", DriveType.Fixed, "/", 500 * G, 100 * G),
            ("/proc", "proc", DriveType.Ram, "", 0, 0),
            ("/run", "tmpfs", DriveType.Ram, "", 2 * G, 2 * G),
            ("/snap/core/1", "squashfs", DriveType.Fixed, "", 1 * G, 0),
            ("/home", "ext4", DriveType.Fixed, "", 500 * G, 100 * G),     // same device bind-mounted
            ("/media/usb", "vfat", DriveType.Removable, "USB", 32 * G, 16 * G),
        ], OsKind.Linux);

        Assert.Equal(["/", "/media/usb"], drives.Select(d => d.Name));
        Assert.Equal("Sistem", drives[0].Label);
        Assert.Equal(80, drives[0].UsedPercent, 1);
    }

    [Fact]
    public void Windows_KeepsLettersAndDefaultsLabel()
    {
        var drives = DriveService.Filter(
        [
            ("C:\\", "NTFS", DriveType.Fixed, "", 100, 40),
            ("D:\\", "NTFS", DriveType.Fixed, "Veri", 100, 40),
        ], OsKind.Windows);
        Assert.Equal(["Yerel Disk", "Veri"], drives.Select(d => d.Label));
    }
}
