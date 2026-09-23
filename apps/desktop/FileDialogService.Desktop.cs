using Omrina.Platform;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Omrina.Desktop;

internal static partial class PlatformFileDialogService
{
    public static partial IFileDialogService Create(Window owner) =>
        new UnoDesktopFileDialogService();
}

/// <summary>
/// Uses Uno's platform picker implementation. Desktop Skia does not expose a
/// Win32 HWND, so this adapter deliberately does not perform the Windows-only
/// InitializeWithWindow call used by the WinAppSDK target.
/// </summary>
internal sealed class UnoDesktopFileDialogService : IFileDialogService
{
    public async Task<IInputImageFile?> PickImageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");

        var file = await picker.PickSingleFileAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return file is null ? null : new LocalInputImageFile(file.Path);
    }

    public async Task<FileSaveResult> SaveTextAsync(
        string suggestedFileName,
        string extension,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedExtension = extension.StartsWith(".", StringComparison.Ordinal)
            ? extension
            : $".{extension}";
        var picker = new FileSavePicker
        {
            SuggestedFileName = suggestedFileName,
            DefaultFileExtension = normalizedExtension
        };
        picker.FileTypeChoices.Add(GetTextFileTypeLabel(normalizedExtension), [normalizedExtension]);

        var file = await picker.PickSaveFileAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (file is null)
        {
            return new FileSaveResult(true, null);
        }

        await FileIO.WriteTextAsync(file, content, Windows.Storage.Streams.UnicodeEncoding.Utf8);
        cancellationToken.ThrowIfCancellationRequested();
        return new FileSaveResult(false, file.Path);
    }

    private static string GetTextFileTypeLabel(string extension) => extension.ToLowerInvariant() switch
    {
        ".svg" => "SVG 图像",
        ".json" => "JSON 文件",
        ".csv" => "CSV 文件",
        _ => "文本文件"
    };
}
