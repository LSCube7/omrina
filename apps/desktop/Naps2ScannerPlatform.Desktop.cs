using NAPS2.Images.ImageSharp;
using NAPS2.Scan;

namespace Omrina.Desktop;

internal static partial class Naps2ScannerPlatform
{
    public static partial ScanningContext CreateContext() =>
        new(new ImageSharpImageContext());

    // NAPS2 resolves Driver.Default to Apple/ImageCaptureCore on macOS and
    // SANE on Linux. No fake device is exposed when the native backend is absent.
    public static partial IReadOnlyList<Driver> GetDrivers() => [Driver.Default];

    public static partial string GetDriverLabel(Driver driver)
    {
        if (OperatingSystem.IsMacOS())
        {
            return "APPLE";
        }

        if (OperatingSystem.IsLinux())
        {
            return "SANE";
        }

        return driver.ToString().ToUpperInvariant();
    }
}
