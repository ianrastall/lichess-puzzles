namespace LichessPuzzleDesktop.Services;

/// <summary>Things the view models need from the windowing shell: pickers, clipboard, dialogs, links.</summary>
public interface IShellService
{
    Task<string?> PickOpenFileAsync(string extension);
    Task<string?> PickSaveFileAsync(string suggestedName, string typeName, string extension);
    void CopyText(string text);
    Task ShowMessageAsync(string title, string message);
    Task OpenUriAsync(Uri uri);
}
