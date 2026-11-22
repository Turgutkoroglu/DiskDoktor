using System.Runtime.InteropServices;

namespace DiskDoktor.Core.Platform;

/// <summary>Size query and emptying of the Windows Recycle Bin (all drives) through shell32.</summary>
public static partial class WindowsRecycleBinApi
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [LibraryImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

    public static (long Bytes, long Items) Query()
    {
        if (!OperatingSystem.IsWindows()) return (0, 0);
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    /// <summary>Returns an error message, or null on success (an already empty bin is success).</summary>
    public static string? Empty()
    {
        if (!OperatingSystem.IsWindows()) return "Yalnız Windows'ta.";
        int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
        return hr == 0 || hr == unchecked((int)0x8000FFFF) ? null : $"Geri Dönüşüm Kutusu boşaltılamadı (0x{hr:X8}).";
    }
}
