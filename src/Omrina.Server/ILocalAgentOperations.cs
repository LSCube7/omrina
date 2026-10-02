using System.Text.Json;
using Omrina.Protocol;

namespace Omrina.Server;

public interface ILocalAgentOperations
{
    Task<JsonElement> GetTemplatesAsync(CancellationToken cancellationToken);
    Task<JsonElement> GetDevicesAsync(CancellationToken cancellationToken);
    Task<JsonElement> RunAsync(TaskOperationRequest request, Stream? image, CancellationToken cancellationToken);
}

public sealed class LocalOperationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
