namespace Omrina.Protocol;

/// <summary>Stable identifiers shared by the local host and its clients.</summary>
public static class HealthProtocol
{
    public const string ServiceName = "omrina-local";
    public const int ProtocolVersion = 1;
    public const string HealthPath = "/health";
}

public sealed record HealthResponse(
    string Service,
    int ProtocolVersion,
    string Status);
