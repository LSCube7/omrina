using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Omrina.Protocol;
using Omrina.Server;

internal static class SubjectiveImageTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var operations = new ImageOperations();
        await using var server = new LoopbackHealthServer(operations, port: 0);
        await server.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(server.BaseAddress!) };
        const string origin = "https://subjective.example";
        var path = $"/v1/subjective-reviews/{operations.ReviewId}/questions/{operations.QuestionId}/image";
        async Task<HttpResponseMessage> Request(string route, string? token = null, string from = origin, string? host = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            request.Headers.Add("Origin", from);
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (host is not null) request.Headers.Host = host;
            return await client.SendAsync(request);
        }
        async Task<GrantResponse> Pair()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/pairing/requests");
            request.Headers.Add("Origin", origin);
            request.Content = new StringContent("{\"clientName\":\"Image test\"}", Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            var ticket = JsonSerializer.Deserialize<PairingTicket>(await response.Content.ReadAsStringAsync(), AgentJson.Options)!;
            using var exchange = new HttpRequestMessage(HttpMethod.Post, "/v1/pairing/exchange");
            exchange.Headers.Add("Origin", origin);
            exchange.Content = new StringContent(JsonSerializer.Serialize(new PairingExchange(ticket.RequestId, server.ApprovePairing(ticket.RequestId)), AgentJson.Options), Encoding.UTF8, "application/json");
            using var exchanged = await client.SendAsync(exchange);
            return JsonSerializer.Deserialize<GrantResponse>(await exchanged.Content.ReadAsStringAsync(), AgentJson.Options)!;
        }
        var owner = await Pair();
        var other = await Pair();
        operations.OwnerGrantId = owner.GrantId;
        using (var response = await Request(path)) check(response.StatusCode == HttpStatusCode.Unauthorized, "region image requires bearer");
        using (var response = await Request(path, owner.Token, "https://unknown.example")) check(response.StatusCode == HttpStatusCode.Forbidden, "region image rejects unapproved origin");
        using (var response = await Request(path, owner.Token, host: "evil.example:" + client.BaseAddress.Port)) check(response.StatusCode == HttpStatusCode.Forbidden, "region image rejects foreign host");
        using (var response = await Request(path, other.Token))
        {
            check(response.StatusCode == HttpStatusCode.NotFound && !(await response.Content.ReadAsStringAsync()).Contains("secret"), "same-origin other grant cannot read region or exception detail");
        }
        using (var response = await Request(path.Replace(operations.QuestionId.ToString(), Guid.NewGuid().ToString()), owner.Token)) check(response.StatusCode == HttpStatusCode.NotFound, "unknown region uses same not-found response");
        using (var response = await Request(path + "?file=private.png", owner.Token)) check(response.StatusCode == HttpStatusCode.BadRequest, "region image rejects query parameters");
        using (var response = await Request("/v1/subjective-reviews/not-a-guid/questions/not-a-guid/image", owner.Token)) check(response.StatusCode == HttpStatusCode.BadRequest, "region image validates identifiers");
        using (var response = await Request(path, owner.Token))
        {
            check(response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "image/png"
                && response.Headers.CacheControl?.NoStore == true
                && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(operations.Bytes), "region image returns exact PNG bytes without caching");
            check(operations.LastGrantId == owner.GrantId, "region adapter receives authenticated grant ID");
        }
        operations.Bytes = new byte[9];
        using (var response = await Request(path, owner.Token)) check(response.StatusCode == HttpStatusCode.InternalServerError, "region image rejects invalid PNG signature");
        operations.Bytes = new byte[8 * 1024 * 1024 + 1];
        ImageOperations.Signature.CopyTo(operations.Bytes, 0);
        using (var response = await Request(path, owner.Token)) check(response.StatusCode == HttpStatusCode.RequestEntityTooLarge, "region image response size bounded");
        operations.ErrorCode = "SUBJECTIVE_IMAGE_TOO_LARGE";
        using (var response = await Request(path, owner.Token)) check(response.StatusCode == HttpStatusCode.RequestEntityTooLarge, "region image maps adapter size error safely");
        operations.ErrorCode = "UNTRUSTED_secret_path";
        using (var response = await Request(path, owner.Token)) check(response.StatusCode == HttpStatusCode.InternalServerError && !(await response.Content.ReadAsStringAsync()).Contains("secret"), "region image sanitizes unknown operation errors");
        operations.ErrorCode = null;
        operations.CancelRead = true;
        using (var response = await Request(path, owner.Token)) check(response.StatusCode == HttpStatusCode.GatewayTimeout
            && (await response.Content.ReadAsStringAsync()).Contains("REGION_IMAGE_TIMEOUT"), "region image cancelled adapter returns safe timeout response");
        operations.CancelRead = false;
        operations.Bytes = ImageOperations.Signature.ToArray();
        operations.Block = true;
        var pending = Enumerable.Range(0, 32).Select(_ => Request(path, owner.Token)).ToArray();
        await operations.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (var response = await Request(path, owner.Token)) check(response.StatusCode == HttpStatusCode.TooManyRequests, "region image read concurrency bounded at 32");
        check(server.RevokeGrant(owner.GrantId), "region image owner grant revoked during read");
        var revokedResponses = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
        check(revokedResponses.All(response => response.StatusCode == HttpStatusCode.Unauthorized), "revoked in-flight reads do not deliver images");
        foreach (var response in revokedResponses) response.Dispose();
        check(operations.ReadToken.IsCancellationRequested, "region adapter token cancelled on grant revocation");
        operations.OwnerGrantId = other.GrantId;
        using (var response = await Request(path, other.Token)) check(response.StatusCode == HttpStatusCode.TooManyRequests, "cancelled adapters retain capacity until they actually finish");
        operations.Release.TrySetResult();
        await using var unavailable = new LoopbackHealthServer(port: 0);
        await unavailable.StartAsync();
        using var unavailableClient = new HttpClient { BaseAddress = new Uri(unavailable.BaseAddress!) };
        using var pairing = new HttpRequestMessage(HttpMethod.Post, "/v1/pairing/requests");
        pairing.Headers.Add("Origin", origin);
        pairing.Content = new StringContent("{\"clientName\":\"Unavailable test\"}", Encoding.UTF8, "application/json");
        using var pairingResponse = await unavailableClient.SendAsync(pairing);
        var unavailableTicket = JsonSerializer.Deserialize<PairingTicket>(await pairingResponse.Content.ReadAsStringAsync(), AgentJson.Options)!;
        using var exchangeRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/pairing/exchange");
        exchangeRequest.Headers.Add("Origin", origin);
        exchangeRequest.Content = new StringContent(JsonSerializer.Serialize(new PairingExchange(unavailableTicket.RequestId, unavailable.ApprovePairing(unavailableTicket.RequestId)), AgentJson.Options), Encoding.UTF8, "application/json");
        using var exchangeResponse = await unavailableClient.SendAsync(exchangeRequest);
        var unavailableGrant = JsonSerializer.Deserialize<GrantResponse>(await exchangeResponse.Content.ReadAsStringAsync(), AgentJson.Options)!;
        using var imageRequest = new HttpRequestMessage(HttpMethod.Get, path);
        imageRequest.Headers.Add("Origin", origin);
        imageRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", unavailableGrant.Token);
        using var unavailableResponse = await unavailableClient.SendAsync(imageRequest);
        check(unavailableResponse.StatusCode == HttpStatusCode.ServiceUnavailable, "missing region adapter reports feature unavailable");
    }

    private sealed class ImageOperations : ILocalAgentOperations, ILocalAgentSubjectiveImages
    {
        public static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
        public Guid ReviewId { get; } = Guid.NewGuid();
        public Guid QuestionId { get; } = Guid.NewGuid();
        public string? OwnerGrantId { get; set; }
        public string? LastGrantId { get; private set; }
        public byte[] Bytes { get; set; } = Signature.ToArray();
        public string? ErrorCode { get; set; }
        public bool Block { get; set; }
        public bool CancelRead { get; set; }
        public CancellationToken ReadToken { get; private set; }
        private int _blockedReads;
        public TaskCompletionSource AllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<LocalSubjectiveImage> ReadSubjectiveImageAsync(string grantId, Guid reviewId, Guid questionId, CancellationToken cancellationToken)
        {
            LastGrantId = grantId;
            if (grantId != OwnerGrantId || reviewId != ReviewId || questionId != QuestionId)
                throw new LocalOperationException("SUBJECTIVE_IMAGE_NOT_FOUND", "secret/private/path");
            if (ErrorCode is not null) throw new LocalOperationException(ErrorCode, "secret/private/path");
            if (CancelRead) throw new OperationCanceledException("secret/private/path");
            if (Block)
            {
                ReadToken = cancellationToken;
                if (Interlocked.Increment(ref _blockedReads) == 32) AllStarted.TrySetResult();
                await Release.Task;
            }
            return new LocalSubjectiveImage(Bytes);
        }
        public Task<JsonElement> GetTemplatesAsync(CancellationToken cancellationToken) => Task.FromResult(JsonSerializer.SerializeToElement(Array.Empty<object>()));
        public Task<JsonElement> GetDevicesAsync(CancellationToken cancellationToken) => GetTemplatesAsync(cancellationToken);
        public Task<JsonElement> RunAsync(TaskOperationRequest request, Stream? image, CancellationToken cancellationToken) => GetTemplatesAsync(cancellationToken);
    }
}
