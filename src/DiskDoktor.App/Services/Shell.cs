using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DiskDoktor.Core.Platform;

namespace DiskDoktor.App.Services;

/// <summary>UI-side OS integration the view model needs, kept behind an interface for tests.</summary>
public interface IShell
{
    string MachineName { get; }
    Task<string?> SaveTextAsync(string suggestedName, string extension, string description, string content);
    void OpenFolder(string path);
    /// <summary>Windows: start again with UAC and close this instance.</summary>
    void RestartElevated();
}

public sealed class AvaloniaShell(Func<TopLevel?> topLevel, ISystemEnvironment env) : IShell
{
    public string MachineName => Environment.MachineName;

    public async Task<string?> SaveTextAsync(string suggestedName, string extension, string description, string content)
    {
        var top = topLevel();
        if (top is null) return null;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(description) { Patterns = [$"*.{extension}"] }],
        });
        if (file is null) return null;
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(true));
        await writer.WriteAsync(content);
        return file.TryGetLocalPath() ?? file.Name;
    }

    public void RestartElevated()
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe) return;
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" })?.Dispose();
            (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception) { /* user said no */ }
    }

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        var psi = new ProcessStartInfo { UseShellExecute = false };
        switch (env.Os)
        {
            case OsKind.Windows: psi.FileName = "explorer.exe"; break;
            case OsKind.MacOS: psi.FileName = "open"; break;
            default: psi.FileName = "xdg-open"; break;
        }
        psi.ArgumentList.Add(path);
        try { Process.Start(psi)?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { /* no file manager available */ }
    }
}
