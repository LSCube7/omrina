using Omrina.Platform;
using Microsoft.UI.Xaml;
using System.Text;
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
    private const int MaximumBundleBytes = 1024 * 1024;
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

    public async Task<string?> PickTemplateBundleJsonAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(
            picker,
            WinRT.Interop.WindowNative.GetWindowHandle(_owner));

        var file = await picker.PickSingleFileAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (file is null)
        {
            return null;
        }

        var properties = await file.GetBasicPropertiesAsync();
        if (properties.Size is 0 or > MaximumBundleBytes)
        {
            throw new InvalidDataException("所选资料包为空或超过 1 MB。");
        }

        var json = await FileIO.ReadTextAsync(file, Windows.Storage.Streams.UnicodeEncoding.Utf8);
        cancellationToken.ThrowIfCancellationRequested();
        json = json.TrimStart('\uFEFF');
        if (Encoding.UTF8.GetByteCount(json) > MaximumBundleBytes)
        {
            throw new InvalidDataException("所选资料包超过 1 MB。");
        }

        return json;
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
            SuggestedFileName = suggestedFileName,
            DefaultFileExtension = extension.StartsWith(".", StringComparison.Ordinal)
                ? extension
                : $".{extension}"
        };
        var normalizedExtension = extension.StartsWith(".", StringComparison.Ordinal)
            ? extension
            : $".{extension}";
        picker.FileTypeChoices.Add(GetTextFileTypeLabel(normalizedExtension), [normalizedExtension]);
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

    private static string GetTextFileTypeLabel(string extension) => extension.ToLowerInvariant() switch
    {
        ".svg" => "SVG 图像",
        ".json" => "JSON 文件",
        ".csv" => "CSV 文件",
        _ => "文本文件"
    };
}
