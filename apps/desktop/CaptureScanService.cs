using NAPS2.Images;
using NAPS2.Scan;

namespace AnswerSheet.Desktop;

/// <summary>A single scan-page file waiting to be copied into CaptureStore.</summary>
internal static class CaptureScanService
{
    private static readonly int[] SupportedResolutions = [150, 300, 600];

    public static async Task<string> ScanFirstPageAsync(
        ScanController scanController,
        ScanDevice device,
        int dpi,
        string temporaryRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scanController);
        ArgumentNullException.ThrowIfNull(device);
        if (!SupportedResolutions.Contains(dpi))
        {
            throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "Only 150, 300 and 600 DPI are supported.");
        }

        if (string.IsNullOrWhiteSpace(temporaryRoot))
        {
            throw new ArgumentException("Temporary scan directory is required.", nameof(temporaryRoot));
        }

        var temporaryDirectory = Path.Combine(Path.GetFullPath(temporaryRoot), "pending-scans");
        Directory.CreateDirectory(temporaryDirectory);
        var outputPath = Path.Combine(
            temporaryDirectory,
            $"scan-{Guid.NewGuid():N}.png");

        try
        {
            var options = new ScanOptions
            {
                Device = device,
                Driver = device.Driver,
                PaperSource = PaperSource.Flatbed,
                PageSize = PageSize.A4,
                Dpi = dpi,
                UseNativeUI = false
            };

            await using var pages = scanController.Scan(options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            if (!await pages.MoveNextAsync())
            {
                throw new InvalidOperationException("扫描仪没有返回图像。");
            }

            using var image = pages.Current;
            image.Save(outputPath, ImageFileFormat.Png, new ImageSaveOptions());
            cancellationToken.ThrowIfCancellationRequested();
            return outputPath;
        }
        catch
        {
            DeleteTemporaryFile(outputPath);
            throw;
        }
    }

    public static void DeleteTemporaryFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The path is generated below the app-owned pending-scans directory.
            // Never touch the user-selected source or a caller-supplied arbitrary path.
            System.Diagnostics.Debug.WriteLine(
                $"Pending scan cleanup failed: {exception.GetType().Name}.");
        }
    }
}
