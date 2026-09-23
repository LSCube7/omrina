using Omrina.Core;

namespace Omrina.Platform;

/// <summary>Decodes a selected image and runs the platform-neutral recognizer.</summary>
public interface IAnswerSheetRecognitionService
{
    Task<RecognitionResult> RecognizeAsync(
        AnswerSheetLayout layout,
        IInputImageFile file,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Skia-backed image adapter. It owns only codec decoding; geometry, page
/// registration, orientation, and fill interpretation stay in Omrina.Core.
/// </summary>
public sealed class SkiaAnswerSheetRecognitionService : IAnswerSheetRecognitionService
{
    public const ulong MaximumInputFileSizeBytes = 100UL * 1024 * 1024;

    private readonly SkiaImageDecoder _decoder;
    private readonly IAnswerSheetRecognizer _recognizer;

    public SkiaAnswerSheetRecognitionService()
        : this(new SkiaImageDecoder(), new AnswerSheetRecognizer())
    {
    }

    public SkiaAnswerSheetRecognitionService(
        SkiaImageDecoder decoder,
        IAnswerSheetRecognizer recognizer)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
    }

    public async Task<RecognitionResult> RecognizeAsync(
        AnswerSheetLayout layout,
        IInputImageFile file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();
        if (file.Length > MaximumInputFileSizeBytes)
        {
            throw new ImageDecodeException(
                $"识别输入文件不能超过 {MaximumInputFileSizeBytes / (1024 * 1024)} MiB。",
                ImageDecodeFailure.FileTooLarge);
        }

        var image = await Task.Run(
            () => _decoder.DecodeGrayscaleAsync(file, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () => _recognizer.Recognize(layout, image, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Alias for UI callers that prefer the decode-and-recognize wording.</summary>
    public Task<RecognitionResult> DecodeAndRecognizeAsync(
        AnswerSheetLayout layout,
        IInputImageFile file,
        CancellationToken cancellationToken = default)
    {
        return RecognizeAsync(layout, file, cancellationToken);
    }

    public Task<RecognitionResult> RecognizeAsync(
        IInputImageFile file,
        AnswerSheetLayout layout,
        CancellationToken cancellationToken = default)
    {
        return RecognizeAsync(layout, file, cancellationToken);
    }

    public Task<RecognitionResult> RecognizeAsync(
        string path,
        AnswerSheetLayout layout,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("图像文件路径不能为空。", nameof(path));
        }

        return RecognizeAsync(layout, new LocalInputImageFile(path), cancellationToken);
    }

    public Task<RecognitionResult> DecodeAndRecognizeAsync(
        string path,
        AnswerSheetLayout layout,
        CancellationToken cancellationToken = default)
    {
        return RecognizeAsync(path, layout, cancellationToken);
    }
}
