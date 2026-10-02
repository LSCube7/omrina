namespace Omrina.Server;

/// <summary>Reads a saved question region within the authenticated grant's resources.</summary>
public interface ILocalAgentSubjectiveImages
{
    Task<LocalSubjectiveImage> ReadSubjectiveImageAsync(
        string grantId, Guid reviewId, Guid questionId, CancellationToken cancellationToken);
}

public sealed record LocalSubjectiveImage(byte[] Bytes);
