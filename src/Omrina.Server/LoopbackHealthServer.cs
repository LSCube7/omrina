using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Omrina.Protocol;

namespace Omrina.Server;

public sealed class LoopbackHealthServer : IAsyncDisposable
{
    public const int Port = 17843;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LocalAgentHost _host;
    private readonly int _port;
    private WebApplication? _application;
    public event Action? PairingStateChanged;
    public event Action<string>? GrantRevoked;
    public IReadOnlyList<PendingPairing> PendingPairings => _host.PendingPairings;
    public IReadOnlyList<GrantSummary> ActiveGrants => _host.ActiveGrants;
    public string? BaseAddress { get; private set; }
    public LoopbackHealthServer(ILocalAgentOperations? operations = null, IEnumerable<string>? allowedOrigins = null, int port = Port)
    {
        _port = port;
        _host = new LocalAgentHost(operations, allowedOrigins ??
            (Environment.GetEnvironmentVariable("OMRINA_ALLOWED_ORIGINS") ?? Environment.GetEnvironmentVariable("ANSWERSHEET_ALLOWED_ORIGINS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        _host.StateChanged += () => PairingStateChanged?.Invoke();
        _host.GrantRevoked += id => GrantRevoked?.Invoke(id);
    }
    public string ApprovePairing(string requestId) => _host.ApprovePairing(requestId);
    public bool RejectPairing(string requestId) => _host.RejectPairing(requestId);
    public bool RevokeGrant(string grantId) => _host.RevokeGrant(grantId);
    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_application is not null) return;
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = 20 * 1024 * 1024;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                options.Listen(IPAddress.Loopback, _port, listen => listen.Protocols = HttpProtocols.Http1);
            });
            var app = builder.Build();
            _host.Map(app);
            try { await app.StartAsync(); }
            catch { await app.DisposeAsync(); throw; }
            _application = app;
            BaseAddress = app.Urls.Single();
        }
        finally { _gate.Release(); }
    }
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var app = _application;
            _application = null;
            _host.Reset();
            BaseAddress = null;
            if (app is null) return;
            try { await app.StopAsync(); } finally { await app.DisposeAsync(); }
        }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync() { await StopAsync(); _gate.Dispose(); }
}
