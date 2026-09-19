using Omrina.Platform;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Omrina.Desktop;

internal static partial class PlatformFileDialogService
{
    public static partial IFileDialogService Create(Window owner) =>
        new WinAppSdkFileDialogService(owner);
}

internal sealed class WinAppSdkFileDialogService : IFileDialogService
{
    private readonly Window _owner;

    public WinAppSdkFileDialogService(Window owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    public async Task<IInputImageFile?> PickImageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(_owner));

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

        var picker = new FileSavePicker
        {
            SuggestedFileName = suggestedFileName
        };
        picker.FileTypeChoices.Add("SVG 图像", [extension]);
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(_owner));

        var file = await picker.PickSaveFileAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (file is null)
        {
            return new FileSaveResult(true, null);
        }

        await FileIO.WriteTextAsync(
            file,
            content,
            Windows.Storage.Streams.UnicodeEncoding.Utf8);
        return new FileSaveResult(false, file.Path);
    }
}
