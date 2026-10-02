using Omrina.Scanning;

namespace Omrina.Desktop;

/// <summary>
/// Serializes scanner access shared by the desktop page and local-agent tasks.
/// A scan refreshes the native device map under the same gate before using an ID.
/// </summary>
internal sealed class SerializedScannerService : IScannerService
{
    private readonly IScannerService _inner;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _disposed;

    public SerializedScannerService(IScannerService inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async Task<IReadOnlyList<ScannerDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await _inner.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<ScanResult> ScanAsync(
        ScannerDevice device,
        ScanOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var currentDevices = await _inner.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
            var currentDevice = currentDevices.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, device.Id, StringComparison.Ordinal));
            if (currentDevice is null)
            {
                throw new InvalidOperationException("扫描设备已失效，请刷新设备列表后重试。");
            }

            return await _inner.ScanAsync(currentDevice, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
