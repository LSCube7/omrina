using NAPS2.Images.Gdi;
using NAPS2.Scan;

namespace Omrina.Desktop;

internal static partial class Naps2ScannerPlatform
{
    public static partial ScanningContext CreateContext()
    {
        var context = new ScanningContext(new GdiImageContext());
        // NAPS2 uses its 32-bit worker for TWAIN while the app remains x64/ARM64.
        context.SetUpWin32Worker();
        return context;
    }

    public static partial IReadOnlyList<Driver> GetDrivers() => [Driver.Wia, Driver.Twain];

    public static partial string GetDriverLabel(Driver driver) => driver.ToString().ToUpperInvariant();
}
