using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using RemoteDesk.Host.Session;

namespace RemoteDesk.Host.Server;

/// <summary>
/// Embedded Kestrel-based WebSocket server that serves the browser client
/// and provides the <c>/stream</c> WebSocket endpoint for LAN connections.
/// </summary>
public sealed class LocalWebSocketServer : IAsyncDisposable
{
    private WebApplication? _app;
    private readonly SessionManager _sessionManager;

    public int Port { get; private set; }
    public bool IsRunning => _app != null;

    public LocalWebSocketServer(SessionManager sessionManager)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
    }

    /// <summary>
    /// Starts the Kestrel server on the specified port.
    /// Serves static browser client files and the /stream WebSocket endpoint.
    /// </summary>
    public async Task StartAsync(int port = 8443, string? certPath = null, string? certPassword = null)
    {
        Port = port;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(opts =>
        {
            opts.ListenAnyIP(port, listenOpts =>
            {
                if (certPath != null)
                    listenOpts.UseHttps(certPath, certPassword ?? string.Empty);
                else
                    listenOpts.UseHttps(); // Development certificate
            });
        });

        // Suppress default ASP.NET Core logging for embedded use
        builder.Logging.ClearProviders();

        _app = builder.Build();

        _app.UseWebSockets();

        // Serve static browser client files from wwwroot
        var wwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(wwwrootPath))
        {
            _app.UseDefaultFiles(new DefaultFilesOptions
            {
                FileProvider = new PhysicalFileProvider(wwwrootPath)
            });
            _app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwrootPath)
            });
        }

        // WebSocket endpoint
        _app.Map("/stream", HandleStream);

        await _app.StartAsync();
    }

    private async Task HandleStream(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsync("WebSocket connections only.");
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        await _sessionManager.HandleNewConnectionAsync(ws);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }
}
