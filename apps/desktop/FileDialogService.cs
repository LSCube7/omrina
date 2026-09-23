using Omrina.Platform;
using Microsoft.UI.Xaml;

namespace Omrina.Desktop;

internal interface IFileDialogService
{
    Task<IInputImageFile?> PickImageAsync(CancellationToken cancellationToken = default);

    Task<FileSaveResult> SaveTextAsync(
        string suggestedFileName,
        string extension,
        string content,
        CancellationToken cancellationToken = default);
}

public sealed record FileSaveResult(bool Cancelled, string? Path);

internal static partial class PlatformFileDialogService
{
    public static partial IFileDialogService Create(Window owner);
}
