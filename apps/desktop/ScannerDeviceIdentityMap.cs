using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Omrina.Desktop;

/// <summary>
/// Maps native scanner identities to anonymous IDs that remain stable only for
/// this process session. The native identity is kept only in the current map.
/// </summary>
internal sealed class ScannerDeviceIdentityMap<TNativeDevice> : IDisposable
{
    private const int MaximumDriverNameLength = 256;
    private const int MaximumNativeIdLength = 4096;

    private readonly object _gate = new();
    private readonly byte[] _sessionKey = RandomNumberGenerator.GetBytes(32);
    private Dictionary<string, TNativeDevice> _currentDevices = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>
    /// Replaces the active mapping. Missing or duplicate native identities are
    /// omitted and counted, so stale public IDs cannot fall back to an index.
    /// </summary>
    public IReadOnlyList<(string DeviceId, TNativeDevice NativeDevice)> ReplaceDevices(
        IEnumerable<(string? Driver, string? NativeId, TNativeDevice NativeDevice)> candidates,
        out int skippedMissingIdentityCount,
        out int skippedDuplicateIdentityCount)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var replacement = new Dictionary<string, TNativeDevice>(StringComparer.Ordinal);
        var entries = new List<(string DeviceId, TNativeDevice NativeDevice)>();
        var ambiguousDeviceIds = new HashSet<string>(StringComparer.Ordinal);
        var skippedMissingIdentity = 0;
        var skippedDuplicateIdentity = 0;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var candidate in candidates)
            {
                if (!TryCreatePublicId(candidate.Driver, candidate.NativeId, out var deviceId))
                {
                    skippedMissingIdentity++;
                    continue;
                }

                if (ambiguousDeviceIds.Contains(deviceId))
                {
                    skippedDuplicateIdentity++;
                    continue;
                }

                if (replacement.ContainsKey(deviceId))
                {
                    replacement.Remove(deviceId);
                    entries.RemoveAll(entry => string.Equals(entry.DeviceId, deviceId, StringComparison.Ordinal));
                    ambiguousDeviceIds.Add(deviceId);
                    skippedDuplicateIdentity += 2;
                    continue;
                }

                replacement.Add(deviceId, candidate.NativeDevice);
                entries.Add((deviceId, candidate.NativeDevice));
            }

            _currentDevices = replacement;
        }

        skippedMissingIdentityCount = skippedMissingIdentity;
        skippedDuplicateIdentityCount = skippedDuplicateIdentity;
        return entries;
    }

    public bool TryGetNativeDevice(string deviceId, out TNativeDevice nativeDevice)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        lock (_gate)
        {
            if (!_disposed && _currentDevices.TryGetValue(deviceId, out nativeDevice!))
            {
                return true;
            }

            nativeDevice = default!;
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _currentDevices.Clear();
            CryptographicOperations.ZeroMemory(_sessionKey);
        }
    }

    private bool TryCreatePublicId(string? driver, string? nativeId, out string deviceId)
    {
        deviceId = string.Empty;
        if (string.IsNullOrWhiteSpace(driver)
            || driver.Length > MaximumDriverNameLength
            || driver.Contains('\0')
            || string.IsNullOrWhiteSpace(nativeId)
            || nativeId.Length > MaximumNativeIdLength)
        {
            return false;
        }

        var normalizedDriver = driver.Trim().ToLowerInvariant();
        var driverBytes = Encoding.UTF8.GetBytes(normalizedDriver);
        var nativeIdBytes = Encoding.UTF8.GetBytes(nativeId);
        var message = new byte[sizeof(int) * 2 + driverBytes.Length + nativeIdBytes.Length];
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(0, sizeof(int)), driverBytes.Length);
        driverBytes.CopyTo(message.AsSpan(sizeof(int)));
        var nativeIdLengthOffset = sizeof(int) + driverBytes.Length;
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(nativeIdLengthOffset, sizeof(int)), nativeIdBytes.Length);
        nativeIdBytes.CopyTo(message.AsSpan(nativeIdLengthOffset + sizeof(int)));

        var digest = HMACSHA256.HashData(_sessionKey, message);
        deviceId = "scanner:" + Convert.ToHexString(digest).ToLowerInvariant();
        CryptographicOperations.ZeroMemory(digest);
        CryptographicOperations.ZeroMemory(message);
        return true;
    }
}
