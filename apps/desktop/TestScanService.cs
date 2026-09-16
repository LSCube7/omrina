using NAPS2.Images;
using NAPS2.Scan;

namespace AnswerSheet.Desktop;

public static class TestScanService
{
    public static async Task<string> ScanFirstPageAsync(
        ScanController scanController,
        ScanDevice device,
        CancellationToken cancellationToken)
    {
        var outputDirectory = GetOutputDirectory();
        Directory.CreateDirectory(outputDirectory);

        var options = new ScanOptions
        {
            Device = device,
            Driver = device.Driver,
            PaperSource = PaperSource.Flatbed,
            PageSize = PageSize.A4,
            Dpi = 300,
            UseNativeUI = false
        };

        await using var pages = scanController.Scan(options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        if (!await pages.MoveNextAsync())
        {
            throw new InvalidOperationException("扫描仪没有返回图像。");
        }

        var outputPath = Path.Combine(
            outputDirectory,
            $"test-scan-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.png");
        using var image = pages.Current;
        image.Save(outputPath, ImageFileFormat.Png, new ImageSaveOptions());
        return outputPath;
    }

    private static string GetOutputDirectory()
    {
        var repositoryRoot = FindRepositoryRoot(Directory.GetCurrentDirectory());
        return repositoryRoot is null
            ? Path.Combine(AppContext.BaseDirectory, "test-scans")
            : Path.Combine(repositoryRoot, "artifacts", "scans");
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(startDirectory)); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
        }

        return null;
    }
}
