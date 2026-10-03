using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Omrina.Protocol;

namespace Omrina.Server;

internal sealed class LocalAgentHost
{
    private const int MaxJson = 65536, MaxImage = 20 * 1024 * 1024;
    private readonly object _sync = new();
    private readonly ILocalAgentOperations? _operations;
    private readonly TimeProvider _timeProvider;
    private readonly HashSet<string> _healthOrigins;
    private readonly Dictionary<string, Pairing> _pairings = new();
    private readonly Dictionary<string, Grant> _grants = new();
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _rates = new();
    private readonly SemaphoreSlim _bodyReaders = new(8, 8);
    private readonly SemaphoreSlim _subjectiveImageReaders = new(32, 32);
    private ITimer? _expiryTimer;
    public event Action? StateChanged;
    public event Action<string>? GrantRevoked;
    public LocalAgentHost(ILocalAgentOperations? operations, IEnumerable<string> origins, TimeProvider timeProvider)
    {
        _operations = operations;
        _timeProvider = timeProvider;
        _healthOrigins = new HashSet<string>(origins.Select(origin => ValidOrigin(origin) ? origin : throw new ArgumentException("Invalid HTTP/HTTPS Origin.")), StringComparer.Ordinal);
    }
    public IReadOnlyList<PendingPairing> PendingPairings { get { lock (_sync) { Prune(); return _pairings.Values.Select(p => p.Summary).ToArray(); } } }
    public IReadOnlyList<GrantSummary> ActiveGrants { get { lock (_sync) { Prune(); return _grants.Values.Select(g => g.Summary).ToArray(); } } }
    public string ApprovePairing(string id)
    {
        string code;
        lock (_sync)
        {
            Prune();
            if (!_pairings.TryGetValue(id, out var p)) throw new InvalidOperationException("Pairing request expired or unavailable.");
            code = Secret(); p.CodeHash = Hash(code); p.Attempts = 0;
        }
        StateChanged?.Invoke(); return code;
    }
    public bool RejectPairing(string id) { bool removed; lock (_sync) removed = _pairings.Remove(id); if (removed) StateChanged?.Invoke(); return removed; }
    public bool RevokeGrant(string id)
    {
        bool removed; Grant? grant;
        lock (_sync) removed = _grants.Remove(id, out grant);
        grant?.Revoke();
        if (removed) { GrantRevoked?.Invoke(id); StateChanged?.Invoke(); } return removed;
    }
    public void Reset()
    {
        _expiryTimer?.Dispose(); _expiryTimer = null;
        Grant[] grants;
        lock (_sync) { grants = _grants.Values.ToArray(); _grants.Clear(); _pairings.Clear(); _rates.Clear(); }
        foreach (var grant in grants) { grant.Revoke(); GrantRevoked?.Invoke(grant.Summary.GrantId); }
        StateChanged?.Invoke();
    }
    private void Prune()
    {
        var now = _timeProvider.GetUtcNow();
        var expiredPairings = _pairings.Values.Where(p => p.Summary.ExpiresAt <= now).ToArray();
        foreach (var p in expiredPairings) _pairings.Remove(p.Summary.RequestId);
        if (expiredPairings.Length > 0) NotifyStateChanged();
        foreach (var g in _grants.Values.Where(g => g.Summary.ExpiresAt <= now).ToArray())
        {
            _grants.Remove(g.Summary.GrantId);
            ThreadPool.QueueUserWorkItem(_ => { g.Revoke(); GrantRevoked?.Invoke(g.Summary.GrantId); StateChanged?.Invoke(); });
        }
        foreach (var key in _rates.Where(p => now - p.Value.Start > TimeSpan.FromMinutes(1)).Select(p => p.Key).ToArray()) _rates.Remove(key);
    }
    private bool Rate(string key, int limit)
    {
        Prune();
        if (!_rates.TryGetValue(key, out var rate)) { if (_rates.Count >= 256) return false; rate = (_timeProvider.GetUtcNow(), 0); }
        _rates[key] = (rate.Start, rate.Count + 1); return rate.Count < limit;
    }
    private void NotifyStateChanged() => ThreadPool.QueueUserWorkItem(_ => StateChanged?.Invoke());
    private static bool ValidOrigin(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == "http" || uri.Scheme == "https") && uri.UserInfo == "" && uri.AbsolutePath == "/"
        && uri.Query == "" && uri.Fragment == "" && uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) == value;
    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    private static bool Matches(byte[]? hash, string value) => hash is not null && value.Length <= 128 && CryptographicOperations.FixedTimeEquals(hash, Hash(value));
    private static IResult Error(int status, string code, string message = "请求无法完成，请检查输入后重试。") => Results.Json(new ProtocolError(code, message), AgentJson.Options, statusCode: status);
    private static IResult Json(object? value, int status = 200) => Results.Json(value, AgentJson.Options, statusCode: status);
    private Grant? Authenticate(HttpContext context, string? token = null)
    {
        var origin = context.Request.Headers.Origin.ToString();
        var authorization = context.Request.Headers.Authorization.ToString();
        token ??= authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization[7..] : "";
        lock (_sync)
        {
            Prune();
            return _grants.Values.FirstOrDefault(g => g.Summary.Origin == origin && Matches(g.TokenHash, token));
        }
    }
    public void Map(WebApplication app)
    {
        app.Lifetime.ApplicationStarted.Register(() => _expiryTimer = _timeProvider.CreateTimer(_ => { lock (_sync) Prune(); }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (request.Host.Port != context.Connection.LocalPort || request.Host.Host is not ("127.0.0.1" or "localhost"))
            { await Error(403, "HOST_DENIED").ExecuteAsync(context); return; }
            var origin = request.Headers.Origin.ToString();
            var pairing = request.Path == "/v1/pairing/requests" || request.Path == "/v1/pairing/exchange";
            var health = request.Path == HealthProtocol.HealthPath;
            bool permitted;
            lock (_sync) { Prune(); permitted = ValidOrigin(origin) && (pairing || (health && _healthOrigins.Contains(origin)) || _grants.Values.Any(g => g.Summary.Origin == origin)); }
            if ((!health || origin.Length > 0) && !permitted) { await Error(403, "ORIGIN_DENIED").ExecuteAsync(context); return; }
            if (permitted) { context.Response.Headers.AccessControlAllowOrigin = origin; context.Response.Headers.Vary = "Origin"; }
            if (request.Method == "OPTIONS")
            {
                var method = request.Headers.AccessControlRequestMethod.ToString();
                var headers = request.Headers.AccessControlRequestHeaders.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var allowedHeaders = new HashSet<string>(["authorization", "content-type", "x-idempotency-key", "x-omrina-parameters"], StringComparer.OrdinalIgnoreCase);
                if (method is not ("GET" or "POST" or "DELETE") || headers.Any(h => !allowedHeaders.Contains(h))) { context.Response.StatusCode = 403; return; }
                context.Response.Headers.AccessControlAllowMethods = "GET, POST, DELETE";
                context.Response.Headers.AccessControlAllowHeaders = "Authorization, Content-Type, X-Idempotency-Key, X-Omrina-Parameters";
                context.Response.StatusCode = 204; return;
            }
            if (!health && !pairing && request.Path != "/v1/events")
            {
                var grant = Authenticate(context);
                if (grant is null) { await Error(401, "UNAUTHORIZED", "请重新配对本地服务。").ExecuteAsync(context); return; }
                context.Items["grant"] = grant;
            }
            try { await next(); }
            catch (BadHttpRequestException ex) { await Error(ex.StatusCode, ex.StatusCode == 429 ? "REQUEST_BUSY" : "REQUEST_TOO_LARGE").ExecuteAsync(context); }
            catch (JsonException) { await Error(400, "INVALID_JSON").ExecuteAsync(context); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (OperationCanceledException) { await Error(408, "REQUEST_TIMEOUT", "请求已超时，请重试。").ExecuteAsync(context); }
            catch (LocalOperationException ex) { await Error(400, ex.Code, ex.Message).ExecuteAsync(context); }
            catch { if (!context.Response.HasStarted) await Error(500, "INTERNAL_ERROR", "操作失败，请稍后重试。").ExecuteAsync(context); }
        });
        app.MapGet(HealthProtocol.HealthPath, () => Json(new HealthResponse(HealthProtocol.ServiceName, HealthProtocol.ProtocolVersion, "ready")));
        app.MapPost("/v1/pairing/requests", async (HttpContext c) =>
        {
            var body = await ReadJson<PairingRequest>(c);
            if (string.IsNullOrWhiteSpace(body.ClientName) || body.ClientName.Length > 100) return Error(400, "INVALID_CLIENT");
            PairingTicket ticket;
            lock (_sync)
            {
                if (!Rate("pair:" + c.Request.Headers.Origin, 10) || _pairings.Count >= 64) return Error(429, "RATE_LIMITED");
                ticket = new(Guid.NewGuid().ToString("N"), _timeProvider.GetUtcNow().AddMinutes(5));
                _pairings.Add(ticket.RequestId, new Pairing(new(ticket.RequestId, c.Request.Headers.Origin.ToString(), body.ClientName, ticket.ExpiresAt)));
            }
            StateChanged?.Invoke(); return Json(ticket, 202);
        });
        app.MapPost("/v1/pairing/exchange", async (HttpContext c) =>
        {
            var body = await ReadJson<PairingExchange>(c);
            if (string.IsNullOrWhiteSpace(body.RequestId) || body.RequestId.Length > 64 || string.IsNullOrWhiteSpace(body.Code) || body.Code.Length > 128) return Error(400, "INVALID_PAIRING_EXCHANGE");
            GrantResponse response;
            lock (_sync)
            {
                if (!Rate("exchange:" + c.Request.Headers.Origin, 10)) return Error(429, "RATE_LIMITED");
                if (!_pairings.TryGetValue(body.RequestId, out var p) || p.Summary.Origin != c.Request.Headers.Origin.ToString()) return Error(401, "PAIRING_DENIED");
                if (!Matches(p.CodeHash, body.Code ?? "")) { if (++p.Attempts >= 5) { _pairings.Remove(body.RequestId); NotifyStateChanged(); } return Error(401, "PAIRING_DENIED"); }
                if (_grants.Count >= 64) return Error(429, "GRANT_LIMIT");
                _pairings.Remove(body.RequestId);
                var token = Secret(); var now = _timeProvider.GetUtcNow();
                var summary = new GrantSummary(Guid.NewGuid().ToString("N"), p.Summary.Origin, p.Summary.ClientName, now, now.AddHours(8));
                _grants.Add(summary.GrantId, new Grant(summary, Hash(token)));
                response = new(summary.GrantId, token, summary.ExpiresAt);
            }
            StateChanged?.Invoke(); return Json(response);
        });
        app.MapDelete("/v1/grant", (HttpContext c) => { RevokeGrant(Current(c).Summary.GrantId); return Results.NoContent(); });
        app.MapGet("/v1/templates", (Func<HttpContext, Task<IResult>>)(c => QueryOperations(c, true)));
        app.MapGet("/v1/devices", (Func<HttpContext, Task<IResult>>)(c => QueryOperations(c, false)));
        app.MapGet("/v1/subjective-reviews/{reviewId}/questions/{questionId}/image", (HttpContext context, string reviewId, string questionId) => ReadSubjectiveImage(context, reviewId, questionId));
        app.MapGet("/v1/subjective-reviews/{reviewId}/groups/{groupId}/image", (HttpContext context, string reviewId, string groupId) =>
            ReadSubjectiveImage(context, reviewId, "", groupId));
        app.MapGet("/v1/captures/{captureId}/groups/{groupId}/image", (HttpContext context, string captureId, string groupId) =>
            ReadSubjectiveImage(context, captureId, "", groupId, true));
        app.MapGet("/v1/tasks", (HttpContext c) => { lock (_sync) return Json(Current(c).Tasks.Values.Select(t => t.Snapshot).ToArray()); });
        app.MapGet("/v1/tasks/{id}", (HttpContext c, string id) => { lock (_sync) return Current(c).Tasks.TryGetValue(id, out var task) ? Json(task.Snapshot) : Error(404, "TASK_NOT_FOUND"); });
        app.MapPost("/v1/tasks/{id}/cancel", (HttpContext c, string id) =>
        {
            Job? task;
            lock (_sync) if (!Current(c).Tasks.TryGetValue(id, out task)) return Error(404, "TASK_NOT_FOUND");
            task.Cancellation.Cancel();
            lock (_sync) return Json(task.Snapshot);
        });
        app.MapPost("/v1/tasks", async (HttpContext c) =>
        {
            var body = await ReadJson<CreateTaskRequest>(c);
            if (!Enum.IsDefined(body.Operation) || body.Operation == TaskOperation.Upload || body.Parameters.ValueKind != JsonValueKind.Object) return Error(400, "INVALID_OPERATION");
            if (body.Operation == TaskOperation.Review && (!body.Parameters.TryGetProperty("expectedVersion", out var version) || !version.TryGetInt32(out var n) || n < 0)) return Error(400, "EXPECTED_VERSION_REQUIRED");
            return CreateTask(Current(c), body.IdempotencyKey, body.Operation, body.Parameters, null);
        });
        app.MapPost("/v1/tasks/upload", async (HttpContext c) =>
        {
            var type = c.Request.ContentType;
            if (type is not ("image/png" or "image/jpeg")) return Error(415, "IMAGE_TYPE_REQUIRED");
            var header = c.Request.Headers["X-Omrina-Parameters"].ToString();
            if (Encoding.UTF8.GetByteCount(header) > 4096 || header.Length == 0) return Error(400, "INVALID_PARAMETERS");
            using var doc = JsonDocument.Parse(header);
            var parameters = doc.RootElement.Clone();
            if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("templateId", out var tid) || tid.ValueKind != JsonValueKind.String || !parameters.TryGetProperty("fileName", out var fn) || fn.ValueKind != JsonValueKind.String) return Error(400, "INVALID_PARAMETERS");
            var bytes = await ReadBody(c, MaxImage);
            if ((type == "image/png" && (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}))) || (type == "image/jpeg" && (bytes.Length < 3 || bytes[0] != 255 || bytes[1] != 216 || bytes[2] != 255))) return Error(400, "INVALID_IMAGE");
            return CreateTask(Current(c), c.Request.Headers["X-Idempotency-Key"].ToString(), TaskOperation.Upload, parameters, bytes);
        });
        app.MapGet("/v1/events", WebSocketAsync);
    }
    private static Grant Current(HttpContext c) => (Grant)c.Items["grant"]!;
    private async Task<IResult> ReadSubjectiveImage(HttpContext context, string reviewId, string questionId, string? groupId = null, bool captureGroup = false)
    {
        context.Response.Headers.CacheControl = "no-store";
        var parsedQuestionId = Guid.Empty;
        var parsedReviewId = Guid.Empty;
        if (context.Request.QueryString.HasValue
            || !Guid.TryParse(reviewId, out parsedReviewId) || parsedReviewId == Guid.Empty
            || (groupId is null && (!Guid.TryParse(questionId, out parsedQuestionId) || parsedQuestionId == Guid.Empty))
            || (groupId is not null && !(groupId.Length is > 0 and <= 80 && char.IsAsciiLetterOrDigit(groupId[0])
                && groupId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))))
            return Error(400, "INVALID_IMAGE_RESOURCE");
        if (_operations is not ILocalAgentSubjectiveImages images)
            return Error(503, "SUBJECTIVE_IMAGES_UNAVAILABLE", "当前本地服务暂不支持读取答题区域。");
        if (!_subjectiveImageReaders.Wait(0)) return Error(429, "REQUEST_BUSY");
        var grant = Current(context);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, grant.Cancellation.Token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(30));
        Task<LocalSubjectiveImage>? reading = null;
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            reading = groupId is null
                ? images.ReadSubjectiveImageAsync(grant.Summary.GrantId, parsedReviewId, parsedQuestionId, lifetime.Token)
                : captureGroup ? images.ReadCaptureGroupImageAsync(grant.Summary.GrantId, reviewId, groupId, lifetime.Token)
                : images.ReadSubjectiveGroupImageAsync(grant.Summary.GrantId, parsedReviewId, groupId, lifetime.Token);
            var image = await reading.WaitAsync(lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            if (image?.Bytes is null || image.Bytes.Length < 8
                || !image.Bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                return Error(500, "INVALID_REGION_IMAGE", "答题区域图像无法读取，请重新采集后重试。");
            if (image.Bytes.Length > 8 * 1024 * 1024)
                return Error(413, "REGION_IMAGE_TOO_LARGE", "答题区域图像过大，请缩小区域后重试。");
            return new SubjectiveImageResult(image.Bytes, grant, _timeProvider);
        }
        catch (OperationCanceledException) when (grant.Cancellation.IsCancellationRequested)
        { return Error(401, "UNAUTHORIZED", "请重新配对本地服务。"); }
        catch (LocalOperationException exception)
        {
            return exception.Code switch
            {
                "NOT_FOUND" or "SUBJECTIVE_REVIEW_NOT_FOUND" or "SUBJECTIVE_QUESTION_NOT_FOUND" or "SUBJECTIVE_GROUP_NOT_FOUND" or "CAPTURE_NOT_FOUND" or "SUBJECTIVE_IMAGE_NOT_FOUND"
                    => Error(404, "SUBJECTIVE_IMAGE_NOT_FOUND", "找不到此授权下的答题区域。"),
                "SUBJECTIVE_IMAGE_TOO_LARGE" => Error(413, "REGION_IMAGE_TOO_LARGE", "答题区域图像过大，请缩小区域后重试。"),
                "INVALID_REGION" => Error(400, "INVALID_REGION", "答题区域无效，请重新指定区域。"),
                _ => Error(500, "REGION_IMAGE_READ_FAILED", "答题区域读取失败，请稍后重试。")
            };
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        { return Error(504, "REGION_IMAGE_TIMEOUT", "答题区域读取超时，请稍后重试。"); }
        catch { return Error(500, "REGION_IMAGE_READ_FAILED", "答题区域读取失败，请稍后重试。"); }
        finally
        {
            // An adapter that ignores cancellation must retain its slot until it finishes.
            if (reading is { IsCompleted: false })
                _ = reading.ContinueWith(task => { _ = task.Exception; _subjectiveImageReaders.Release(); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else _subjectiveImageReaders.Release();
        }
    }

    private sealed class SubjectiveImageResult(byte[] bytes, Grant grant, TimeProvider timeProvider) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, grant.Cancellation.Token);
            if (grant.Cancellation.IsCancellationRequested || grant.Summary.ExpiresAt <= timeProvider.GetUtcNow())
            { await Error(401, "UNAUTHORIZED", "请重新配对本地服务。").ExecuteAsync(context); return; }
            lifetime.Token.ThrowIfCancellationRequested();
            context.Response.Headers.CacheControl = "no-store";
            context.Response.ContentType = "image/png";
            context.Response.ContentLength = bytes.Length;
            try { await context.Response.Body.WriteAsync(bytes, lifetime.Token); }
            catch (OperationCanceledException) when (grant.Cancellation.IsCancellationRequested)
            {
                if (context.Response.HasStarted) context.Abort();
                else
                {
                    context.Response.ContentLength = null;
                    context.Response.ContentType = null;
                    await Error(401, "UNAUTHORIZED", "请重新配对本地服务。").ExecuteAsync(context);
                }
            }
        }
    }
    private async Task<IResult> QueryOperations(HttpContext c, bool templates)
    {
        if (_operations is null) return Error(503, "OPERATIONS_UNAVAILABLE");
        var grant = Current(c);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted, grant.Cancellation.Token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var result = await (templates ? _operations.GetTemplatesAsync(lifetime.Token) : _operations.GetDevicesAsync(lifetime.Token)).WaitAsync(lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested(); return Json(result);
        }
        catch (OperationCanceledException) when (grant.Cancellation.IsCancellationRequested) { return Error(401, "UNAUTHORIZED"); }
    }
    private async Task<byte[]> ReadBody(HttpContext c, int limit)
    {
        if (!_bodyReaders.Wait(0)) throw new BadHttpRequestException("Request busy", 429);
        try
        {
        if (c.Request.ContentLength > limit) throw new BadHttpRequestException("Request too large", 413);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var stream = new MemoryStream(); var buffer = new byte[8192];
        while (true) { var count = await c.Request.Body.ReadAsync(buffer, timeout.Token); if (count == 0) break; if (stream.Length + count > limit) throw new BadHttpRequestException("Request too large", 413); await stream.WriteAsync(buffer.AsMemory(0, count), timeout.Token); }
        return stream.ToArray();
        }
        finally { _bodyReaders.Release(); }
    }
    private async Task<T> ReadJson<T>(HttpContext c) => JsonSerializer.Deserialize<T>(await ReadBody(c, MaxJson), AgentJson.Options) ?? throw new JsonException();
    private IResult CreateTask(Grant grant, string key, TaskOperation operation, JsonElement parameters, byte[]? image)
    {
        if (_operations is null) return Error(503, "OPERATIONS_UNAVAILABLE");
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128) return Error(400, "IDEMPOTENCY_KEY_REQUIRED");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(operation + ":" + parameters.GetRawText())); if (image is not null) hash.AppendData(image);
        var fingerprint = hash.GetHashAndReset();
        Job job;
        lock (_sync)
        {
            if (grant.Cancellation.IsCancellationRequested) return Error(401, "UNAUTHORIZED");
            if (grant.Keys.TryGetValue(key, out var existing)) return CryptographicOperations.FixedTimeEquals(existing.Fingerprint, fingerprint) ? Json(existing.Snapshot, 202) : Error(409, "IDEMPOTENCY_CONFLICT");
            var allTasks = _grants.Values.SelectMany(g => g.Tasks.Values).ToArray();
            var active = allTasks.Where(t => t.Snapshot.Status is LocalTaskStatus.Queued or LocalTaskStatus.Running).ToArray();
            if (!Rate("task:" + grant.Summary.GrantId, 60) || grant.Tasks.Count >= 256 || allTasks.Length >= 512 || active.Length >= 32 || (image is not null && active.Count(t => t.Snapshot.Operation == TaskOperation.Upload) >= 4)) return Error(429, "TASK_LIMIT");
            var now = _timeProvider.GetUtcNow();
            job = new(new(Guid.NewGuid().ToString("N"), operation, LocalTaskStatus.Queued, null, null, now, now), fingerprint, CancellationTokenSource.CreateLinkedTokenSource(grant.Cancellation.Token));
            grant.Tasks.Add(job.Snapshot.TaskId, job); grant.Keys.Add(key, job);
            Publish(grant, job.Snapshot);
        }
        _ = RunJob(grant, job, parameters.Clone(), image);
        return Json(job.Snapshot, 202);
    }
    private async Task RunJob(Grant grant, Job job, JsonElement parameters, byte[]? bytes)
    {
        job.Cancellation.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            Update(grant, job, LocalTaskStatus.Running);
            using var image = bytes is null ? null : new MemoryStream(bytes, writable: false);
            var result = await _operations!.RunAsync(new(grant.Summary.GrantId, job.Snapshot.Operation, parameters), image, job.Cancellation.Token);
            job.Cancellation.Token.ThrowIfCancellationRequested();
            if (Encoding.UTF8.GetByteCount(result.GetRawText()) > 1024 * 1024) throw new LocalOperationException("RESULT_TOO_LARGE", "结果过大，请减少处理内容后重试。");
            Update(grant, job, LocalTaskStatus.Completed, result.Clone());
        }
        catch (OperationCanceledException) { Update(grant, job, LocalTaskStatus.Cancelled); }
        catch (LocalOperationException ex) { Update(grant, job, LocalTaskStatus.Failed, error: new(ex.Code, ex.Message)); }
        catch { Update(grant, job, LocalTaskStatus.Failed, error: new("OPERATION_FAILED", "操作失败，请检查输入后重试。")); }
    }
    private void Update(Grant grant, Job job, LocalTaskStatus status, JsonElement? result = null, ProtocolError? error = null)
    { lock (_sync) { job.Snapshot = job.Snapshot with { Status = status, Result = result, Error = error, UpdatedAt = _timeProvider.GetUtcNow() }; Publish(grant, job.Snapshot); } }
    private static void Publish(Grant grant, TaskSnapshot task)
    { var evt = new AgentEvent("task", ++grant.Sequence, task); foreach (var listener in grant.Listeners.ToArray()) if (!listener.TryPublish(evt)) grant.Listeners.Remove(listener); }
    private async Task WebSocketAsync(HttpContext c)
    {
        if (!c.WebSockets.IsWebSocketRequest || c.Request.QueryString.HasValue) { c.Response.StatusCode = 400; return; }
        using var socket = await c.WebSockets.AcceptWebSocketAsync();
        Grant? grant = null; EventSubscription? events = null;
        var connectionCloseStatus = WebSocketCloseStatus.PolicyViolation;
        Task<WebSocketReceiveResult>? receiving = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var buffer = new byte[1024];
            receiving = socket.ReceiveAsync(buffer, c.RequestAborted);
            var received = await receiving.WaitAsync(deadline.Token);
            if (!received.EndOfMessage || received.MessageType != WebSocketMessageType.Text) return;
            using var auth = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
            if (auth.RootElement.ValueKind != JsonValueKind.Object || !auth.RootElement.TryGetProperty("type", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != "authenticate" || !auth.RootElement.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String) return;
            grant = Authenticate(c, token.GetString()); if (grant is null) return;
            long sequence;
            lock (_sync)
            {
                if (grant.Cancellation.IsCancellationRequested) return;
                if (grant.Listeners.Count >= 8)
                {
                    connectionCloseStatus = (WebSocketCloseStatus)1013;
                    return;
                }
                events = new EventSubscription();
                grant.Listeners.Add(events); sequence = grant.Sequence;
            }
            using var live = CancellationTokenSource.CreateLinkedTokenSource(c.RequestAborted, grant.Cancellation.Token); live.CancelAfter(grant.Summary.ExpiresAt - _timeProvider.GetUtcNow());
            await Send(socket, new AgentEvent("authenticated", sequence), live.Token);
            receiving = socket.ReceiveAsync(new byte[1024], c.RequestAborted);
            while (true)
            {
                var available = events.Reader.WaitToReadAsync(live.Token).AsTask();
                if (await Task.WhenAny(available, receiving) == receiving || !await available) break;
                while (events.Reader.TryRead(out var evt)) await Send(socket, evt, live.Token);
            }
            live.Cancel();
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (JsonException) { }
        finally
        {
            if (grant is not null && events is not null) lock (_sync) { grant.Listeners.Remove(events); events.Complete(); }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    var closeStatus = grant is null || grant.Cancellation.IsCancellationRequested || grant.Summary.ExpiresAt <= _timeProvider.GetUtcNow()
                        ? WebSocketCloseStatus.PolicyViolation
                        : events?.CloseStatus ?? connectionCloseStatus;
                    await socket.CloseOutputAsync(closeStatus, "Session ended", closing.Token);
                    if (receiving is null || receiving.IsCompleted) receiving = socket.ReceiveAsync(new byte[1024], closing.Token);
                    if (receiving is not null) await receiving.WaitAsync(closing.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { socket.Abort(); }
            }
        }
    }
    private static Task Send(WebSocket socket, AgentEvent evt, CancellationToken cancellation) => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(evt, AgentJson.Options), WebSocketMessageType.Text, true, cancellation);
    private sealed class Pairing(PendingPairing summary) { public PendingPairing Summary = summary; public byte[]? CodeHash; public int Attempts; }
    private sealed class Job(TaskSnapshot snapshot, byte[] fingerprint, CancellationTokenSource cancellation)
    { public TaskSnapshot Snapshot = snapshot; public byte[] Fingerprint = fingerprint; public CancellationTokenSource Cancellation = cancellation; }
    private sealed class Grant(GrantSummary summary, byte[] tokenHash)
    {
        public GrantSummary Summary = summary; public byte[] TokenHash = tokenHash;
        public CancellationTokenSource Cancellation = new();
        public Dictionary<string, Job> Tasks = new(); public Dictionary<string, Job> Keys = new();
        public List<EventSubscription> Listeners = new(); public long Sequence;
        public void Revoke() => Cancellation.Cancel();
    }
}
