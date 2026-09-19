using Omrina.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Printing;
using Windows.Foundation;
using Windows.Graphics.Printing;

namespace Omrina.Desktop;

internal sealed class TemplatePrintController : IDisposable
{
    private const double A4WidthDips = AnswerSheetLayout.PageWidthMm * TemplateRenderer.DipsPerMillimetre;
    private const double A4HeightDips = AnswerSheetLayout.PageHeightMm * TemplateRenderer.DipsPerMillimetre;
    private const double PageSizeToleranceDips = 2;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Action<string> _reportStatus;
    private readonly PrintManager _printManager;
    private readonly PrintDocument _printDocument;
    private readonly IPrintDocumentSource _documentSource;
    private readonly IntPtr _windowHandle;
    private AnswerSheetLayout? _printLayout;
    private Canvas? _printPage;
    private PrintTask? _activeTask;
    private bool _printSessionActive;
    private bool _disposed;

    public TemplatePrintController(Window owner, Action<string> reportStatus)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _reportStatus = reportStatus ?? throw new ArgumentNullException(nameof(reportStatus));
        _dispatcherQueue = owner.DispatcherQueue;
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(owner);

        _printDocument = new PrintDocument();
        _printDocument.Paginate += PrintDocument_Paginate;
        _printDocument.GetPreviewPage += PrintDocument_GetPreviewPage;
        _printDocument.AddPages += PrintDocument_AddPages;
        _documentSource = _printDocument.DocumentSource;

        _printManager = PrintManagerInterop.GetForWindow(_windowHandle);
        _printManager.PrintTaskRequested += PrintManager_PrintTaskRequested;
    }

    public bool IsBusy => _printSessionActive;

    public async Task RequestPrintAsync(AnswerSheetLayout layout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(layout);
        if (_printSessionActive)
        {
            throw new InvalidOperationException("打印会话正在进行，请等待系统打印窗口结束后再试。");
        }

        _printSessionActive = true;
        _printLayout = layout;
        _printPage = null;
        ReportStatus("正在打开系统打印设置。请在系统窗口中选择 A4 纵向纸张。");

        try
        {
            var accepted = await PrintManagerInterop.ShowPrintUIForWindowAsync(_windowHandle);
            if (!accepted)
            {
                ClearPrintSession();
                ReportStatus("已取消打印。未向打印机发送内容。");
            }
        }
        catch
        {
            ClearPrintSession();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _printManager.PrintTaskRequested -= PrintManager_PrintTaskRequested;
        _printDocument.Paginate -= PrintDocument_Paginate;
        _printDocument.GetPreviewPage -= PrintDocument_GetPreviewPage;
        _printDocument.AddPages -= PrintDocument_AddPages;
        ClearPrintSession();
    }

    private void PrintManager_PrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        if (_disposed || !_printSessionActive || _printLayout is null)
        {
            return;
        }

        try
        {
            var printTask = args.Request.CreatePrintTask("OMRINA 模板", sourceArgs =>
            {
                sourceArgs.SetSource(_documentSource);
            });
            if (_activeTask is not null)
            {
                _activeTask.Completed -= PrintTask_Completed;
            }

            _activeTask = printTask;
            _activeTask.Completed += PrintTask_Completed;
        }
        catch
        {
            ClearPrintSession();
            ReportStatus("无法创建系统打印任务，请稍后重试。");
        }
    }

    private void PrintDocument_Paginate(object sender, PaginateEventArgs args)
    {
        try
        {
            var layout = _printLayout ?? throw new InvalidOperationException("没有可打印的模板。");
            var description = args.PrintTaskOptions.GetPageDescription(0);
            ValidateA4PortraitPage(description);
            ValidatePrintableArea(description.ImageableRect, TemplateRenderer.GetContentBounds(layout, TemplateRenderer.DipsPerMillimetre));

            _printPage = new Canvas
            {
                Width = A4WidthDips,
                Height = A4HeightDips
            };
            TemplateRenderer.Render(layout, _printPage, TemplateRenderer.DipsPerMillimetre, showPageOutline: false);
            _printPage.Measure(new Size(A4WidthDips, A4HeightDips));
            _printPage.Arrange(new Rect(0, 0, A4WidthDips, A4HeightDips));
            _printDocument.SetPreviewPageCount(1, PreviewPageCountType.Final);
        }
        catch (Exception exception)
        {
            _printPage = null;
            _printDocument.SetPreviewPageCount(0, PreviewPageCountType.Final);
            var rootCause = exception.GetBaseException();
            ReportStatus($"无法打印：{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。请确认 A4 纵向纸张和可打印边距。");
        }
    }

    private void PrintDocument_GetPreviewPage(object sender, GetPreviewPageEventArgs args)
    {
        if (args.PageNumber == 1 && _printPage is not null)
        {
            _printDocument.SetPreviewPage(1, _printPage);
        }
    }

    private void PrintDocument_AddPages(object sender, AddPagesEventArgs args)
    {
        if (_printPage is null)
        {
            ReportStatus("无法打印：当前纸张或可打印边距不支持该 A4 模板。");
            _printDocument.AddPagesComplete();
            return;
        }

        _printDocument.AddPage(_printPage);
        _printDocument.AddPagesComplete();
    }

    private void PrintTask_Completed(PrintTask sender, PrintTaskCompletedEventArgs args)
    {
        var status = args.Completion switch
        {
            PrintTaskCompletion.Submitted => "系统已接收打印任务。打印是否完成请以系统状态为准。",
            PrintTaskCompletion.Canceled or PrintTaskCompletion.Abandoned => "系统已取消打印任务。",
            PrintTaskCompletion.Failed => "系统无法接收打印任务。",
            _ => "系统已结束打印任务。"
        };
        ClearPrintSession();
        ReportStatus(status);
    }

    private static void ValidateA4PortraitPage(PrintPageDescription description)
    {
        var pageSize = description.PageSize;
        if (Math.Abs(pageSize.Width - A4WidthDips) > PageSizeToleranceDips
            || Math.Abs(pageSize.Height - A4HeightDips) > PageSizeToleranceDips)
        {
            throw new InvalidOperationException("请在系统打印设置中选择 A4 纵向纸张；模板不会自动缩放到其他纸张。");
        }
    }

    private static void ValidatePrintableArea(Rect imageableRect, Rect contentBounds)
    {
        if (contentBounds.Left < imageableRect.Left
            || contentBounds.Top < imageableRect.Top
            || contentBounds.Right > imageableRect.Right
            || contentBounds.Bottom > imageableRect.Bottom)
        {
            throw new InvalidOperationException("当前打印机的可打印边距会裁切模板内容；请调整纸张或选择支持该边距的打印机。");
        }
    }

    private void ReportStatus(string message)
    {
        if (_disposed)
        {
            return;
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            _reportStatus(message);
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed)
            {
                _reportStatus(message);
            }
        });
    }

    private void ClearPrintSession()
    {
        if (_activeTask is not null)
        {
            _activeTask.Completed -= PrintTask_Completed;
            _activeTask = null;
        }

        _printSessionActive = false;
        _printLayout = null;
        _printPage = null;
    }
}
