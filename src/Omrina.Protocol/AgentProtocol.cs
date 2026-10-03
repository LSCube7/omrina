using System.Text.Json;
using System.Text.Json.Serialization;

namespace Omrina.Protocol;

public enum TaskOperation
{
    Template,
    Upload,
    Scan,
    Recognize,
    Score,
    Review,
    Export,
    SubjectiveCreate,
    SubjectiveRead,
    SubjectiveGrade,
    SubjectiveExport,
    SchoolTemplateExport,
    SchoolTemplateImport
}
public enum LocalTaskStatus { Queued, Running, Completed, Failed, Cancelled }
public sealed record PairingRequest(string ClientName);
public sealed record PairingTicket(string RequestId, DateTimeOffset ExpiresAt);
public sealed record PairingExchange(string RequestId, string Code);
public sealed record GrantResponse(string GrantId, string Token, DateTimeOffset ExpiresAt);
public sealed record PendingPairing(string RequestId, string Origin, string ClientName, DateTimeOffset ExpiresAt);
public sealed record CreateTaskRequest(string IdempotencyKey, TaskOperation Operation, JsonElement Parameters);
public sealed record TaskOperationRequest(string GrantId, TaskOperation Operation, JsonElement Parameters);
public sealed record ProtocolError(string Code, string Message);
public sealed record TaskSnapshot(string TaskId, TaskOperation Operation, LocalTaskStatus Status,
    JsonElement? Result, ProtocolError? Error, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record AgentEvent(string Type, long Sequence, TaskSnapshot? Task = null);
public static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false));
        return options;
    }
}

public sealed record GrantSummary(string GrantId, string Origin, string ClientName, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
