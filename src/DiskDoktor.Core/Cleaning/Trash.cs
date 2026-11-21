using System.Globalization;
using System.Text;
using DiskDoktor.Core.Platform;

namespace DiskDoktor.Core.Cleaning;

/// <summary>Moves a file or folder to the OS trash so the user can restore it.</summary>
public interface ITrash
{
    bool IsAvailable { get; }
    void MoveToTrash(string path);
}

public static class TrashFactory
{
    public static ITrash Create(ISystemEnvironment env) => env.Os switch
    {
        OsKind.Windows => new WindowsRecycleBin(),
        OsKind.MacOS => new MacTrash(Path.Combine(env.HomeDirectory, ".Trash")),
        _ => new FreedesktopTrash(Path.Combine(
            env.GetVariable("XDG_DATA_HOME") ?? Path.Combine(env.HomeDirectory, ".local", "share"), "Trash")),
    };
}

public sealed class WindowsRecycleBin : ITrash
{
    public bool IsAvailable => OperatingSystem.IsWindows();

    public void MoveToTrash(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        const Microsoft.VisualBasic.FileIO.UIOption ui = Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs;
        const Microsoft.VisualBasic.FileIO.RecycleOption bin = Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin;
        if (Directory.Exists(path))
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, ui, bin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
        else
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, ui, bin, Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
    }
}

/// <summary>freedesktop.org Trash spec (GNOME, KDE, XFCE...): files/ + info/*.trashinfo so "Restore" works.</summary>
public sealed class FreedesktopTrash(string trashDir) : ITrash
{
    public bool IsAvailable => true;

    public void MoveToTrash(string path)
    {
        string files = Path.Combine(trashDir, "files");
        string info = Path.Combine(trashDir, "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);

        string baseName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        string name = baseName;
        FileStream? infoStream = null;
        for (int i = 2; infoStream is null; i++)
        {
            try
            {
                // CreateNew reserves the name atomically, as the spec requires.
                infoStream = new FileStream(Path.Combine(info, name + ".trashinfo"), FileMode.CreateNew, FileAccess.Write);
            }
            catch (IOException) when (i < 10_000)
            {
                name = $"{baseName}.{i}";
            }
        }

        string infoPath = infoStream.Name;
        try
        {
            using (var w = new StreamWriter(infoStream, new UTF8Encoding(false)))
            {
                w.Write("[Trash Info]\n");
                w.Write($"Path={EncodePath(Path.GetFullPath(path))}\n");
                w.Write($"DeletionDate={DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}\n");
            }
            string dest = Path.Combine(files, name);
            if (Directory.Exists(path)) Directory.Move(path, dest);   // throws across file systems: reported, nothing lost
            else File.Move(path, dest);
        }
        catch
        {
            try { File.Delete(infoPath); } catch { /* best effort */ }
            throw;
        }
    }

    internal static string EncodePath(string path)
    {
        var sb = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(path))
        {
            char c = (char)b;
            if (b < 128 && (char.IsAsciiLetterOrDigit(c) || "/-_.~".Contains(c))) sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}

public sealed class MacTrash(string trashDir) : ITrash
{
    public bool IsAvailable => Directory.Exists(trashDir);

    public void MoveToTrash(string path)
    {
        string baseName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        string dest = Path.Combine(trashDir, baseName);
        for (int i = 2; File.Exists(dest) || Directory.Exists(dest); i++)
            dest = Path.Combine(trashDir, $"{baseName} {i}");
        if (Directory.Exists(path)) Directory.Move(path, dest);
        else File.Move(path, dest);
    }
}
