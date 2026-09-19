using Omrina.Platform;
using Omrina.Scanning;
using Microsoft.UI.Xaml;

namespace Omrina.Desktop;

/// <summary>
/// Entry point for the desktop head's platform services. The UI receives
/// platform-neutral contracts and never constructs WIA/TWAIN/SANE/ICA types.
/// </summary>
public static class DesktopPlatformFactory
{
    internal static IFileDialogService CreateFileDialogs(Window owner) =>
        PlatformFileDialogService.Create(owner);

    public static IScannerService CreateScanner(string temporaryRoot) =>
        new Naps2ScannerService(temporaryRoot);

    public static IInputImageFile OpenImageFile(string path) =>
        new LocalInputImageFile(path);

    public static void DeleteTemporaryScanFile(string path) =>
        CaptureScanService.DeleteTemporaryFile(path);
}
