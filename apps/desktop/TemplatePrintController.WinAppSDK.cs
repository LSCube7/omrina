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
    private const double PageSizeToleranceDips = 2;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Action<string> _reportStatus;
    private readonly PrintManager _printManager;
    private readonly PrintDocument _printDocument;
    private readonly IPrintDocumentSource _documentSource;
    private readonly IntPtr _windowHandle;
    private IReadOnlyList<AnswerSheetLayout>? _printLayouts;
    private readonly List<Canvas> _printPages = [];
    private PrintTask? _activeTask;
    private bool _printSessionActive;
    private bool _disposed;
    private TaskCompletionSource<bool> _printIdle = CreateCompletedSource();

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

    public Task WaitForIdleAsync() => _printIdle.Task;

    public Task RequestPrintAsync(AnswerSheetLayout layout) => RequestPrintAsync(new[] { layout });

    public async Task RequestPrintAsync(IReadOnlyList<AnswerSheetLayout> layouts)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(layouts);
        if (layouts.Count == 0) throw new ArgumentException("没有可打印页面。", nameof(layouts));
        if (_printSessionActive)
        {
            throw new InvalidOperationException("打印会话正在进行，请等待系统打印窗口结束后再试。");
        }

        _printSessionActive = true;
        _printIdle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _printLayouts = layouts.ToArray();
        _printPages.Clear();
        ReportStatus("正在打开系统打印设置。请确认所选纸张和方向与答题卡一致；输出使用真实毫米尺寸。");

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
        if (_disposed || !_printSessionActive || _printLayouts is null)
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

            var first = _printLayouts[0];
            printTask.Options.MediaSize = first.WidthMm > 210 ? PrintMediaSize.IsoA3 : PrintMediaSize.IsoA4;
            printTask.Options.Orientation = first.WidthMm > 210 ? PrintOrientation.Landscape : PrintOrientation.Portrait;
            if (first.SchoolDefinition?.Duplex == true)
                printTask.Options.Duplex = first.WidthMm > 210
                    ? PrintDuplex.TwoSidedShortEdge
                    : PrintDuplex.TwoSidedLongEdge;
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
            var layouts = _printLayouts ?? throw new InvalidOperationException("没有可打印的模板。");
            _printPages.Clear();
            for (var index = 0; index < layouts.Count; index++)
            {
                var layout = layouts[index];
                var width = layout.WidthMm * TemplateRenderer.DipsPerMillimetre;
                var height = layout.HeightMm * TemplateRenderer.DipsPerMillimetre;
                var description = args.PrintTaskOptions.GetPageDescription((uint)index);
                ValidatePage(description, width, height);
                ValidatePrintableArea(description.ImageableRect, TemplateRenderer.GetContentBounds(layout, TemplateRenderer.DipsPerMillimetre));
                var page = new Canvas { Width = width, Height = height };
                TemplateRenderer.Render(layout, page, TemplateRenderer.DipsPerMillimetre, showPageOutline: false);
                page.Measure(new Size(width, height));
                page.Arrange(new Rect(0, 0, width, height));
                _printPages.Add(page);
            }
            _printDocument.SetPreviewPageCount(_printPages.Count, PreviewPageCountType.Final);
        }
        catch (Exception exception)
        {
            _printPages.Clear();
            _printDocument.SetPreviewPageCount(0, PreviewPageCountType.Final);
            var rootCause = exception.GetBaseException();
            ReportStatus($"无法打印：{rootCause.GetType().Name}（0x{rootCause.HResult:X8}）。请确认纸张、方向和可打印边距。");
        }
    }

    private void PrintDocument_GetPreviewPage(object sender, GetPreviewPageEventArgs args)
    {
        if (args.PageNumber >= 1 && args.PageNumber <= _printPages.Count)
            _printDocument.SetPreviewPage(args.PageNumber, _printPages[args.PageNumber - 1]);
    }

    private void PrintDocument_AddPages(object sender, AddPagesEventArgs args)
    {
        if (_printPages.Count == 0) ReportStatus("无法打印：当前纸张或边距不支持所选答题卡。");
        foreach (var page in _printPages) _printDocument.AddPage(page);
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

    private static void ValidatePage(PrintPageDescription description, double width, double height)
    {
        var pageSize = description.PageSize;
        if (Math.Abs(pageSize.Width - width) > PageSizeToleranceDips
            || Math.Abs(pageSize.Height - height) > PageSizeToleranceDips)
        {
            throw new InvalidOperationException("请在系统打印设置中选择匹配的纸张和方向；答题卡不会自动缩放。");
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
        _printLayouts = null;
        _printPages.Clear();
        _printIdle.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> CreateCompletedSource()
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.TrySetResult(true);
        return source;
    }
}
