using Microsoft.UI.Xaml;
using Omrina.Core;

namespace Omrina.Desktop;

/// <summary>
/// Print capability for the Uno Skia desktop head. The WinAppSDK print dialog
/// is Windows-only; macOS/Linux surface an explicit capability error until a
/// native print adapter is added, instead of reporting a false submission.
/// </summary>
internal sealed class TemplatePrintController : IDisposable
{
    private readonly Action<string> _reportStatus;
    private bool _disposed;

    public TemplatePrintController(Window owner, Action<string> reportStatus)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _reportStatus = reportStatus ?? throw new ArgumentNullException(nameof(reportStatus));
    }

    public bool IsBusy => false;

    public Task WaitForIdleAsync() => Task.CompletedTask;

    public Task RequestPrintAsync(AnswerSheetLayout layout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(layout);

        const string message = "当前 macOS/Linux 桌面版本暂未提供系统打印适配器；模板仍可保存为 SVG。";
        _reportStatus(message);
        return Task.FromException(
            new PlatformNotSupportedException(message));
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
