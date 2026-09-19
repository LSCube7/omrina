using NAPS2.Scan;

namespace Omrina.Desktop;

/// <summary>
/// Selects the NAPS2 image context and native driver set for the current head.
/// Implementations are included by Uno.Sdk only for their matching target.
/// </summary>
internal static partial class Naps2ScannerPlatform
{
    public static partial ScanningContext CreateContext();

    public static partial IReadOnlyList<Driver> GetDrivers();

    public static partial string GetDriverLabel(Driver driver);
}
