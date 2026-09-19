using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using System.Net;

using Omrina.Protocol;

namespace Omrina.Server;

public sealed class LoopbackHealthServer : IAsyncDisposable
{
    public const int Port = 17843;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private WebApplication? _application;

    public async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_application is not null)
            {
                return;
            }

            var application = CreateApplication();
            try
            {
                await application.StartAsync();
                _application = application;
            }
            catch
            {
                await application.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var application = _application;
            _application = null;
            if (application is null)
            {
                return;
            }

            try
            {
                await application.StopAsync();
            }
            finally
            {
                await application.DisposeAsync();
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycleGate.Dispose();
    }

    private static WebApplication CreateApplication()
    {
        var allowedOrigins = AllowedOrigins.FromEnvironment();
        var builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, Port, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
            });
        });

        var application = builder.Build();
        application.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("Origin", out var originHeader))
            {
                var origin = originHeader.ToString();
                if (!allowedOrigins.Contains(origin))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                context.Response.Headers.AccessControlAllowOrigin = origin;
                context.Response.Headers.Vary = "Origin";
            }

            await next();
        });

        application.MapGet(HealthProtocol.HealthPath, () => Results.Json(new HealthResponse(
            Service: HealthProtocol.ServiceName,
            ProtocolVersion: HealthProtocol.ProtocolVersion,
            Status: "ready")));

        return application;
    }

    private sealed class AllowedOrigins
    {
        private readonly HashSet<string> _origins;

        private AllowedOrigins(HashSet<string> origins)
        {
            _origins = origins;
        }

        public static AllowedOrigins FromEnvironment()
        {
            var configuredOrigins = Environment.GetEnvironmentVariable("OMRINA_ALLOWED_ORIGINS")
                ?? Environment.GetEnvironmentVariable("ANSWERSHEET_ALLOWED_ORIGINS");
            var origins = new HashSet<string>(StringComparer.Ordinal);

            foreach (var value in (configuredOrigins ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                origins.Add(NormalizeOrigin(value));
            }

            return new AllowedOrigins(origins);
        }

        public bool Contains(string origin)
        {
            try
            {
                return _origins.Contains(NormalizeOrigin(origin));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static string NormalizeOrigin(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrEmpty(uri.Host)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || uri.AbsolutePath != "/"
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new ArgumentException("允许的来源必须是完整的 HTTP 或 HTTPS Origin。", nameof(value));
            }

            return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        }
    }
}
