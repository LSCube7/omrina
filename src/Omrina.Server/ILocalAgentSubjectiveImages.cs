namespace Omrina.Server;

/// <summary>Reads a saved question region within the authenticated grant's resources.</summary>
public interface ILocalAgentSubjectiveImages
{
    Task<LocalSubjectiveImage> ReadSubjectiveImageAsync(
        string grantId, Guid reviewId, Guid questionId, CancellationToken cancellationToken);
    Task<LocalSubjectiveImage> ReadSubjectiveGroupImageAsync(string grantId, Guid reviewId, string groupId, CancellationToken cancellationToken)
        => Task.FromException<LocalSubjectiveImage>(new LocalOperationException("NOT_FOUND", "题组图像不可用。"));
    Task<LocalSubjectiveImage> ReadCaptureGroupImageAsync(string grantId, string captureId, string groupId, CancellationToken cancellationToken)
        => Task.FromException<LocalSubjectiveImage>(new LocalOperationException("NOT_FOUND", "题组图像不可用。"));
}

public sealed record LocalSubjectiveImage(byte[] Bytes);
