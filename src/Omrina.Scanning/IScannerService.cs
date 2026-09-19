namespace Omrina.Scanning;

/// <summary>Platform-neutral description of a scanner exposed by an adapter.</summary>
public sealed record ScannerDevice(
    string Id,
    string Name,
    string Driver);

/// <summary>Common options accepted by a scanner adapter.</summary>
public sealed record ScanOptions(
    int Dpi = 300,
    bool Flatbed = true,
    string PageSize = "A4");

/// <summary>One completed page returned by a scanner adapter.</summary>
public sealed record ScanResult(
    string ImagePath,
    int Dpi,
    string PageSize,
    bool Flatbed);

/// <summary>
/// Cross-platform scanner contract. Driver APIs such as WIA, TWAIN, SANE and ICA
/// remain inside platform-specific adapters.
/// </summary>
public interface IScannerService : IAsyncDisposable
{
    Task<IReadOnlyList<ScannerDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);

    Task<ScanResult> ScanAsync(
        ScannerDevice device,
        ScanOptions options,
        CancellationToken cancellationToken = default);
}
