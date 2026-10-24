using System.Diagnostics;
using Jupiter.LichessPuzzles.Interop;

namespace Jupiter.LichessPuzzles.App.ViewModels;

/// <summary>Owns the puzzle core for the shell and runs its calls off the UI thread.</summary>
public sealed class CoreHost
{
    public PuzzleCore? Core { get; private set; }

    /// <summary>Receives a description of a failed command.</summary>
    public Action<string>? ReportProblem { get; set; }

    public void Attach(PuzzleCore core) => Core = core;

    public PuzzleCore? Detach()
    {
        var core = Core;
        Core = null;
        return core;
    }

    /// <summary>Runs a core call on the thread pool. Returns default when there is no core.</summary>
    public async Task<T?> Run<T>(Func<PuzzleCore, T> call)
    {
        if (Core is not { } core)
        {
            return default;
        }
        return await Task.Run(() => call(core));
    }

    /// <summary>Runs a short command, reporting a failure instead of throwing.</summary>
    public async Task<bool> Try(Action<PuzzleCore> action, string what)
    {
        if (Core is not { } core)
        {
            return false;
        }
        try
        {
            await Task.Run(() => action(core));
            return true;
        }
        catch (PuzzleCoreException e)
        {
            ReportProblem?.Invoke($"{what}: {Format.Sentence(e.Message)}");
            return false;
        }
    }

    public void OpenFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ReportProblem?.Invoke($"Opening {folder} failed: {e.Message}");
        }
    }

    public void OpenLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            ReportProblem?.Invoke($"Opening the link failed: {e.Message}");
        }
    }
}

/// <summary>Number, size and date formatting shared by the views.</summary>
public static class Format
{
    public static string Count(ulong n) => n.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    public static string Count(long n) => n.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);

    public static string Megabytes(ulong bytes) => $"{bytes / 1048576.0:0.0} MB";

    /// <summary>Capitalizes the first letter and ends with a full stop.</summary>
    public static string Sentence(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return text;
        }
        text = char.ToUpperInvariant(text[0]) + text[1..];
        return text.EndsWith('.') ? text : text + ".";
    }

    /// <summary>"Fri, 02 Oct 2026 08:51:45 GMT" → "2 October 2026".</summary>
    public static string HttpDate(string value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
            ? date.ToString("d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture)
            : value;

    public static string UnixDate(long seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("d MMMM yyyy, HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    public static string Duration(double seconds) => seconds switch
    {
        < 60 => $"about {Math.Max(1, Math.Round(seconds)):0} s",
        < 3600 => $"about {Math.Round(seconds / 60):0} min",
        _ => $"about {seconds / 3600:0.0} h",
    };
}
