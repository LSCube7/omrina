using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Omrina.Protocol;
using Omrina.Server;

var harness = args.Contains("--browser-harness");
var operations = new TestOperations();
await using var server = new LoopbackHealthServer(operations, ["http://localhost:3000"], harness ? 17844 : 0);
await server.StartAsync();
if (harness)
{
    Console.WriteLine("Browser harness at " + server.BaseAddress + "; commands: pending, approve <requestId>, revoke <grantId>, grants, quit");
    while (Console.ReadLine() is { } line && line != "quit")
    {
        var command = line.Split(' ', 2);
        try
        {
            if (command[0] == "pending") Console.WriteLine(JsonSerializer.Serialize(server.PendingPairings, AgentJson.Options));
            if (command[0] == "grants") Console.WriteLine(JsonSerializer.Serialize(server.ActiveGrants, AgentJson.Options));
            if (command[0] == "approve" && command.Length == 2) Console.WriteLine("One-time pairing code: " + server.ApprovePairing(command[1]));
            if (command[0] == "revoke" && command.Length == 2) Console.WriteLine(server.RevokeGrant(command[1]));
        }
        catch (InvalidOperationException) { Console.WriteLine("Request expired or unavailable."); }
    }
    return;
}
using var client = new HttpClient { BaseAddress = new Uri(server.BaseAddress!) };
const string origin = "http://localhost:3000";
var assertions = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); assertions++; Console.WriteLine("PASS " + name); }
async Task<HttpResponseMessage> Request(string method, string path, object? body = null, string? token = null, string? from = origin, byte[]? image = null, string? key = null)
{
    using var request = new HttpRequestMessage(new HttpMethod(method), server.BaseAddress + path);
    if (from is not null) request.Headers.Add("Origin", from);
    if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    if (image is not null)
    {
        request.Content = new ByteArrayContent(image); request.Content.Headers.ContentType = new("image/png");
        request.Headers.Add("X-Idempotency-Key", key ?? "upload");
        request.Headers.Add("X-Omrina-Parameters", "{\"templateId\":\"template-1\",\"fileName\":\"test.png\"}");
    }
    else if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, AgentJson.Options), Encoding.UTF8, "application/json");
    return await client.SendAsync(request);
}
async Task<T> Read<T>(HttpResponseMessage response) => JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(), AgentJson.Options)!;
async Task<GrantResponse> Pair(string from = origin)
{
    using var ticketResponse = await Request("POST", "/v1/pairing/requests", new PairingRequest("Test browser"), from: from);
    Check(ticketResponse.StatusCode == HttpStatusCode.Accepted, "pairing requested " + from);
    var ticket = await Read<PairingTicket>(ticketResponse);
    var code = server.ApprovePairing(ticket.RequestId);
    using var response = await Request("POST", "/v1/pairing/exchange", new PairingExchange(ticket.RequestId, code), from: from);
    Check(response.StatusCode == HttpStatusCode.OK, "desktop-confirmed exchange");
    return await Read<GrantResponse>(response);
}
using (var response = await Request("GET", "/health", from: null)) Check(response.IsSuccessStatusCode, "health without origin");
using (var response = await Request("GET", "/health", from: "http://evil.example")) Check(response.StatusCode == HttpStatusCode.Forbidden, "health allowlist preserved");
using (var request = new HttpRequestMessage(HttpMethod.Get, "/health"))
{ request.Headers.Host = "evil.example:" + client.BaseAddress!.Port; using var response = await client.SendAsync(request); Check(response.StatusCode == HttpStatusCode.Forbidden, "host rebinding denied"); }
using (var response = await Request("POST", "/v1/pairing/requests", new PairingRequest("evil"), from: "null")) Check(response.StatusCode == HttpStatusCode.Forbidden, "null origin denied");
var grant = await Pair();
var other = await Pair("https://other.example");
using (var response = await Request("POST", "/v1/pairing/exchange", new { code = "missing-id" })) Check(response.StatusCode == HttpStatusCode.BadRequest, "missing pairing request ID is 400");
using (var response = await Request("POST", "/v1/pairing/requests", new PairingRequest(new string('x', 70000)))) Check(response.StatusCode == HttpStatusCode.RequestEntityTooLarge, "JSON body bounded");
using (var rejected = await Request("POST", "/v1/pairing/requests", new PairingRequest("rejected"), from: "https://rejected.example"))
{
    var ticket = await Read<PairingTicket>(rejected); Check(server.RejectPairing(ticket.RequestId), "desktop reject");
    using var response = await Request("POST", "/v1/pairing/exchange", new PairingExchange(ticket.RequestId, "code"), from: "https://rejected.example"); Check(response.StatusCode == HttpStatusCode.Unauthorized, "rejected pairing cannot exchange");
}
using (var response = await Request("GET", "/v1/templates", token: grant.Token, from: "https://other.example")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "token origin binding");
using (var response = await Request("GET", "/v1/devices")) Check(response.StatusCode == HttpStatusCode.Unauthorized, "bearer required");
using (var response = await Request("GET", "/v1/templates", token: grant.Token)) Check(response.IsSuccessStatusCode && (await Read<JsonElement>(response)).GetProperty("capabilities").GetArrayLength() == 1, "template capabilities response");
using (var response = await Request("GET", "/v1/devices", token: grant.Token)) Check(response.IsSuccessStatusCode && (await Read<JsonElement>(response)).GetArrayLength() == 0, "empty device response");
using (var request = new HttpRequestMessage(HttpMethod.Options, "/v1/tasks/upload"))
{
    request.Headers.Add("Origin", origin); request.Headers.Add("Access-Control-Request-Method", "POST"); request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,x-idempotency-key,x-omrina-parameters");
    using var response = await client.SendAsync(request); Check(response.StatusCode == HttpStatusCode.NoContent && response.Headers.GetValues("Access-Control-Allow-Origin").Single() == origin, "CORS preflight");
}
using var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Origin", origin);
await socket.ConnectAsync(new Uri(server.BaseAddress!.Replace("http://", "ws://") + "/v1/events"), CancellationToken.None);
await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "authenticate", token = grant.Token })), WebSocketMessageType.Text, true, CancellationToken.None);
async Task<JsonElement> Receive(ClientWebSocket ws)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var bytes = new byte[8192]; var received = await ws.ReceiveAsync(bytes, timeout.Token);
    if (received.MessageType == WebSocketMessageType.Close) { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", timeout.Token); return JsonSerializer.SerializeToElement(new { type = "closed" }); }
    return JsonDocument.Parse(bytes.AsMemory(0, received.Count)).RootElement.Clone();
}
Check((await Receive(socket)).GetProperty("type").GetString() == "authenticated", "WS first-frame authentication");
using (var unauthenticated = new ClientWebSocket())
{
    unauthenticated.Options.SetRequestHeader("Origin", origin);
    await unauthenticated.ConnectAsync(new Uri(server.BaseAddress!.Replace("http://", "ws://") + "/v1/events"), CancellationToken.None);
    Check((await Receive(unauthenticated)).GetProperty("type").GetString() == "closed", "WS auth deadline closes without business events");
}
using (var malformed = new ClientWebSocket())
{
    malformed.Options.SetRequestHeader("Origin", origin);
    await malformed.ConnectAsync(new Uri(server.BaseAddress!.Replace("http://", "ws://") + "/v1/events"), CancellationToken.None);
    await malformed.SendAsync(Encoding.UTF8.GetBytes("{\"type\":42,\"token\":7}"), WebSocketMessageType.Text, true, CancellationToken.None);
    Check((await Receive(malformed)).GetProperty("type").GetString() == "closed", "WS malformed auth safely rejected");
}
var parameters = JsonSerializer.SerializeToElement(new { title = "Test", questionCount = 2, optionsPerQuestion = 4 });
var create = new CreateTaskRequest("same-key", TaskOperation.Template, parameters);
using var createdResponse = await Request("POST", "/v1/tasks", create, grant.Token);
var created = await Read<TaskSnapshot>(createdResponse);
Check(createdResponse.StatusCode == HttpStatusCode.Accepted, "task creation");
var queued = await Receive(socket); var running = await Receive(socket); var completed = await Receive(socket);
Check(queued.GetProperty("sequence").GetInt64() < running.GetProperty("sequence").GetInt64() && running.GetProperty("sequence").GetInt64() < completed.GetProperty("sequence").GetInt64(), "per-grant event sequence");
using (var response = await Request("POST", "/v1/tasks", create, grant.Token)) Check((await Read<TaskSnapshot>(response)).TaskId == created.TaskId && operations.Runs == 1, "idempotent create runs once");
using (var response = await Request("POST", "/v1/tasks", create with { Parameters = JsonSerializer.SerializeToElement(new { title = "different" }) }, grant.Token)) Check(response.StatusCode == HttpStatusCode.Conflict, "different parameters conflict");
using (var response = await Request("GET", "/v1/tasks/" + created.TaskId, token: other.Token, from: "https://other.example")) Check(response.StatusCode == HttpStatusCode.NotFound, "task isolation");
using (var response = await Request("GET", "/v1/tasks", token: other.Token, from: "https://other.example")) Check((await Read<TaskSnapshot[]>(response)).Length == 0, "list isolation");
byte[] image = [137,80,78,71,13,10,26,10,0];
using (var response = await Request("POST", "/v1/tasks/upload", token: grant.Token, image: image)) Check(response.StatusCode == HttpStatusCode.Accepted, "PNG upload");
using (var response = await Request("POST", "/v1/tasks/upload", token: grant.Token, image: [137,80,78,71,13,10,26,10,1])) Check(response.StatusCode == HttpStatusCode.Conflict, "upload bytes conflict");
using (var request = new HttpRequestMessage(HttpMethod.Post, "/v1/tasks/upload"))
{
    request.Headers.Add("Origin", origin); request.Headers.Authorization = new("Bearer", grant.Token);
    request.Headers.Add("X-Idempotency-Key", "large-header"); request.Headers.Add("X-Omrina-Parameters", new string('x', 4097));
    request.Content = new ByteArrayContent(image); request.Content.Headers.ContentType = new("image/png");
    using var response = await client.SendAsync(request); Check(response.StatusCode == HttpStatusCode.BadRequest, "upload metadata header bounded");
}
using (var response = await Request("POST", "/v1/tasks", new CreateTaskRequest("review", TaskOperation.Review, JsonSerializer.SerializeToElement(new { resultId = "a" })), grant.Token)) Check(response.StatusCode == HttpStatusCode.BadRequest, "review version required");
using var failureResponse = await Request("POST", "/v1/tasks", new CreateTaskRequest("failure", TaskOperation.Export, parameters), grant.Token);
var failure = await Read<TaskSnapshot>(failureResponse);
for (var i = 0; i < 100 && failure.Status is LocalTaskStatus.Running or LocalTaskStatus.Queued; i++)
{ await Task.Delay(10); using var response = await Request("GET", "/v1/tasks/" + failure.TaskId, token: grant.Token); failure = await Read<TaskSnapshot>(response); }
Check(failure.Status == LocalTaskStatus.Failed && !JsonSerializer.Serialize(failure).Contains("secret-path"), "exceptions do not expose paths");
using var scanResponse = await Request("POST", "/v1/tasks", new CreateTaskRequest("scan", TaskOperation.Scan, parameters), grant.Token);
var scan = await Read<TaskSnapshot>(scanResponse);
using (var response = await Request("POST", "/v1/tasks/" + scan.TaskId + "/cancel", token: grant.Token)) Check(response.IsSuccessStatusCode, "independent task cancellation");
await operations.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check(true, "operation receives cancellation");
server.RevokeGrant(grant.GrantId);
while ((await Receive(socket)).GetProperty("type").GetString() != "closed") { }
Check(true, "revoke closes WS");
using (var response = await Request("GET", "/v1/tasks", token: grant.Token)) Check(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, "revoked token denied");
using var ticketResponse2 = await Request("POST", "/v1/pairing/requests", new PairingRequest("bad codes"));
var ticket2 = await Read<PairingTicket>(ticketResponse2); var correctCode = server.ApprovePairing(ticket2.RequestId);
for (var i = 0; i < 5; i++) { using var response = await Request("POST", "/v1/pairing/exchange", new PairingExchange(ticket2.RequestId, "wrong")); Check(response.StatusCode == HttpStatusCode.Unauthorized, "wrong code denied " + i); }
using (var response = await Request("POST", "/v1/pairing/exchange", new PairingExchange(ticket2.RequestId, correctCode))) Check(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests, "code locked after attempts");
for (var i = 0; i < 11; i++)
{
    using var response = await Request("POST", "/v1/pairing/requests", new PairingRequest("limited"), from: "https://limited.example");
    if (i == 10) Check(response.StatusCode == HttpStatusCode.TooManyRequests, "pairing rate limited");
}
var slowGrant = await Pair("https://slow.example"); operations.SlowDevices = true;
var query = Request("GET", "/v1/devices", token: slowGrant.Token, from: "https://slow.example");
await operations.QueryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); server.RevokeGrant(slowGrant.GrantId);
using (var response = await query) Check(response.StatusCode == HttpStatusCode.Unauthorized, "revoke cancels pending device query");
await operations.QueryCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check(true, "device operation receives grant cancellation");
operations.SlowDevices = false;
for (var i = 0; i < 32; i++)
{
    using var response = await Request("POST", "/v1/tasks", new CreateTaskRequest("capacity-" + i, TaskOperation.Scan, parameters), other.Token, "https://other.example");
    Check(response.StatusCode == HttpStatusCode.Accepted, "active task accepted " + i);
}
using (var response = await Request("POST", "/v1/tasks", new CreateTaskRequest("capacity-over", TaskOperation.Scan, parameters), other.Token, "https://other.example")) Check(response.StatusCode == HttpStatusCode.TooManyRequests, "active task capacity bounded");
HttpStatusCode capacityStatus = HttpStatusCode.Accepted;
for (var i = 0; i < 70 && capacityStatus == HttpStatusCode.Accepted; i++)
{ using var response = await Request("POST", "/v1/pairing/requests", new PairingRequest("capacity"), from: $"https://capacity-{i}.example"); capacityStatus = response.StatusCode; }
Check(capacityStatus == HttpStatusCode.TooManyRequests && server.PendingPairings.Count == 64, "global pairing capacity bounded");
await server.StopAsync(); await server.StartAsync();
for (var i = 0; i < 100 && operations.CancelCount < 33; i++) await Task.Delay(10);
Check(operations.CancelCount >= 33, "stop cancels all active operations");
using (var response = await Request("GET", "/v1/tasks", token: other.Token, from: "https://other.example")) Check(response.StatusCode == HttpStatusCode.Forbidden, "restart invalidates grants");
Console.WriteLine($"Server HTTP/WS checks passed: {assertions}");

sealed class TestOperations : ILocalAgentOperations
{
    public int Runs;
    public TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int CancelCount;
    public bool SlowDevices;
    public TaskCompletionSource QueryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource QueryCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<JsonElement> GetTemplatesAsync(CancellationToken cancellationToken) => Task.FromResult(JsonSerializer.SerializeToElement(new { capabilities = new[] { "template" } }));
    public async Task<JsonElement> GetDevicesAsync(CancellationToken cancellationToken)
    {
        if (SlowDevices)
        {
            QueryStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { QueryCancelled.TrySetResult(); throw; }
        }
        return JsonSerializer.SerializeToElement(Array.Empty<object>());
    }
    public async Task<JsonElement> RunAsync(TaskOperationRequest request, Stream? image, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Runs);
        if (request.Operation == TaskOperation.Export) throw new InvalidOperationException("C:/secret-path/token");
        if (request.Operation == TaskOperation.Scan)
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Interlocked.Increment(ref CancelCount); Cancelled.TrySetResult(); throw; }
        }
        if (image is not null) { using var copy = new MemoryStream(); await image.CopyToAsync(copy, cancellationToken); }
        return JsonSerializer.SerializeToElement(new { grantId = request.GrantId, templateId = "template-1", resourceId = Guid.NewGuid().ToString("N") });
    }
}
