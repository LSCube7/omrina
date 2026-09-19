using NAPS2.Scan;
using Omrina.Scanning;

namespace Omrina.Desktop;

/// <summary>
/// NAPS2 adapter for the platform-neutral scanner contract.
/// NAPS2 and native driver types stay inside this file and the matching platform
/// implementation selected by Naps2ScannerPlatform.
/// </summary>
internal sealed class Naps2ScannerService : IScannerService
{
    private readonly string _temporaryRoot;
    private readonly ScanningContext _scanningContext;
    private readonly ScanController _scanController;
    private readonly Dictionary<string, (ScanDevice Device, Driver Driver)> _nativeDevices = new(StringComparer.Ordinal);
    private bool _disposed;

    public Naps2ScannerService(string temporaryRoot)
    {
        if (string.IsNullOrWhiteSpace(temporaryRoot))
        {
            throw new ArgumentException("Temporary scan directory is required.", nameof(temporaryRoot));
        }

        _temporaryRoot = Path.GetFullPath(temporaryRoot);
        _scanningContext = Naps2ScannerPlatform.CreateContext();
        _scanController = new ScanController(_scanningContext);
    }

    public async Task<IReadOnlyList<ScannerDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        _nativeDevices.Clear();
        var devices = new List<ScannerDevice>();

        foreach (var driver in Naps2ScannerPlatform.GetDrivers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nativeDevices = await _scanController.GetDeviceList(driver);
            var driverIndex = 0;
            foreach (var nativeDevice in nativeDevices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = $"{driver.ToString().ToLowerInvariant()}:{driverIndex++}";
                _nativeDevices.Add(id, (nativeDevice, driver));
                devices.Add(new ScannerDevice(id, nativeDevice.Name, Naps2ScannerPlatform.GetDriverLabel(driver)));
            }
        }

        return devices;
    }

    public async Task<ScanResult> ScanAsync(
        ScannerDevice device,
        Omrina.Scanning.ScanOptions options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);

        if (!_nativeDevices.TryGetValue(device.Id, out var nativeDevice))
        {
            throw new InvalidOperationException("扫描设备已失效，请刷新设备列表后重试。");
        }

        if (options.Dpi is not (150 or 300 or 600))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Dpi, "Only 150, 300 and 600 DPI are supported.");
        }

        if (!options.Flatbed || !string.Equals(options.PageSize, "A4", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("当前扫描适配器仅支持 A4 平板扫描。");
        }

        var imagePath = await CaptureScanService.ScanFirstPageAsync(
            _scanController,
            nativeDevice.Device,
            options.Dpi,
            _temporaryRoot,
            cancellationToken);

        return new ScanResult(imagePath, options.Dpi, options.PageSize, options.Flatbed);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _nativeDevices.Clear();
            _scanningContext.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
