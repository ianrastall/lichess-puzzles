using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace LichessPuzzleDesktop.Services;

public sealed class ShellService(Window window) : IShellService
{
    public async Task<string?> PickOpenFileAsync(string extension)
    {
        var picker = new FileOpenPicker(window.AppWindow.Id);
        picker.FileTypeFilter.Add(extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName, string typeName, string extension)
    {
        var picker = new FileSavePicker(window.AppWindow.Id)
        {
            SuggestedFileName = suggestedName,
            DefaultFileExtension = extension,
        };
        picker.FileTypeChoices.Add(typeName, [extension]);
        return (await picker.PickSaveFileAsync())?.Path;
    }

    public void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush(); // keep the text available after the app closes
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        if (window.Content?.XamlRoot is not { } root) return;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            XamlRoot = root,
            RequestedTheme = ElementTheme.Dark,
        };
        await dialog.ShowAsync();
    }

    public async Task OpenUriAsync(Uri uri) => await Windows.System.Launcher.LaunchUriAsync(uri);
}
