using System.Net;
using System.Text;
using System.Text.Json;
using MyNotch.Models;

namespace MyNotch.Services;

/// <summary>
/// Loopback webhook listener on http://127.0.0.1:9999/ (PRD section 3.2, PLAN Phase 2).
/// HttpListener over HTTP.sys instead of ASP.NET Core/Kestrel: no reflection-heavy
/// stack, keeping Native AOT and idle memory intact (AGENTS.md).
/// Routes: POST /agent (MyNotch payload) -> 202/400/403/405/413; POST /claude/hook (raw Claude Code hook input) -> 202;
/// POST /claude/permission (PermissionRequest hook input) -> 200 with the hook's decision JSON or an empty body;
/// GET /health -> 200; anything else -> 404.
/// Events fire on a listener thread; subscribers must marshal to the UI thread.
/// </summary>
public sealed class WebhookServerService : IDisposable
{
    public const int Port = 9999;
    private readonly int _port;

    /// <summary>The app always listens on <see cref="Port"/>; tests pass a free port.</summary>
    public WebhookServerService(int port = Port) => _port = port;
    internal const int MaxBodyBytes = 16 * 1024;
    internal const int MaxHookBodyBytes = 2 * 1024 * 1024;

    /// <summary>A Claude Code SessionEnd hook: the session (by <see cref="AgentSessions.KeyOf"/> key) is gone.</summary>
    public event EventHandler<string>? SessionEnded;

    /// <summary>
    /// Decides a forwarded permission prompt. Called on a listener thread; may take as long as the user needs
    /// (the implementation applies its own timeout). <see cref="ApprovalDecision.None"/> lets Claude Code ask normally.
    /// </summary>
    public Func<ApprovalRequest, Task<ApprovalDecision>>? ApprovalHandler { get; set; }

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event EventHandler<AgentPayload>? PayloadReceived;

    public bool IsRunning => _listener?.IsListening == true;

    public Task StartAsync()
    {
        if (IsRunning) return Task.CompletedTask;

        _cts = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        // Throws HttpListenerException (e.g. missing URLACL) to the caller on purpose:
        // MainWindow surfaces it instead of failing silently.
        _listener.Start();
        _loop = Task.Run(() => AcceptLoopAsync(_listener, _cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_listener is null) return;
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        try { _listener.Close(); } catch (ObjectDisposedException) { }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MyNotch] Webhook accept failed: {ex.Message}");
                break;
            }

            _ = Task.Run(() => HandleAsync(context), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        try
        {
            var path = request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
            if (path.Length == 0) path = "/";

            if (path == "/health")
            {
                if (request.HttpMethod != "GET") { await RespondAsync(context, 405, "method_not_allowed").ConfigureAwait(false); return; }
                await RespondAsync(context, 200, "{\"status\":\"ok\"}").ConfigureAwait(false);
                return;
            }

            if (path is not ("/agent" or "/claude/hook" or "/claude/permission")) { await RespondAsync(context, 404, "not_found").ConfigureAwait(false); return; }
            if (request.HttpMethod != "POST") { await RespondAsync(context, 405, "method_not_allowed").ConfigureAwait(false); return; }
            // Browsers send Origin on every cross-site POST, and a text/plain POST needs no CORS preflight, so any
            // web page could otherwise spoof agent alerts. Local CLIs and hook scripts never send Origin.
            if (request.Headers["Origin"] is not null) { await RespondAsync(context, 403, "forbidden_origin").ConfigureAwait(false); return; }
            // Claude Code hook input carries tool input (e.g. a whole file for Write), so it gets a larger cap.
            int limit = path == "/agent" ? MaxBodyBytes : MaxHookBodyBytes;
            if (request.ContentLength64 > limit) { await RespondAsync(context, 413, "payload_too_large").ConfigureAwait(false); return; }
            var body = await ReadBodyAsync(request, limit).ConfigureAwait(false);
            if (body is null) { await RespondAsync(context, 413, "payload_too_large").ConfigureAwait(false); return; }

            if (path == "/agent") { await HandleAgentAsync(context, body).ConfigureAwait(false); return; }

            JsonDocument document;
            try { document = JsonDocument.Parse(body); }
            catch (JsonException) { await RespondAsync(context, 400, "invalid_json").ConfigureAwait(false); return; }
            using (document)
            {
                var now = DateTimeOffset.Now;
                if (path == "/claude/hook")
                {
                    switch (ClaudeHooks.Map(document.RootElement, now))
                    {
                        case ClaudeHookEvent.Update update:
                            var (pid, hwnd) = Sender(request);
                            PayloadReceived?.Invoke(this, update.Payload with { Pid = update.Payload.Pid ?? pid, Hwnd = update.Payload.Hwnd ?? hwnd });
                            break;
                        case ClaudeHookEvent.End end:
                            SessionEnded?.Invoke(this, end.SessionKey);
                            break;
                    }
                    // Empty body: command hooks forward stdout to Claude Code, which must not see anything here.
                    await RespondAsync(context, 202, "", "text/plain").ConfigureAwait(false);
                    return;
                }

                // /claude/permission: hold the request until the user decides in the notch, or until the wait runs out.
                var approval = ClaudeHooks.ParsePermission(document.RootElement, now);
                if (approval is null) { await RespondAsync(context, 400, "invalid_payload").ConfigureAwait(false); return; }
                var decision = ApprovalDecision.None;
                if (ApprovalHandler is { } handler)
                {
                    var (pid, hwnd) = Sender(request);
                    try { decision = await handler(approval with { Pid = pid, Hwnd = hwnd }).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { decision = ApprovalDecision.None; }
                }
                await RespondAsync(context, 200, ClaudeHooks.Response(decision)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"[MyNotch] Webhook request failed: {ex.Message}");
            try { context.Response.Abort(); } catch { }
        }
        catch (Exception ex)
        {
            // A subscriber or an odd-but-valid JSON shape threw. Answer anyway: an unanswered request leaves the
            // hook's curl hanging (up to 10 minutes for a permission prompt) and the task's exception unobserved.
            System.Diagnostics.Debug.WriteLine($"[MyNotch] Webhook handler failed: {ex}");
            try { await RespondAsync(context, 500, "{\"error\":\"internal\"}").ConfigureAwait(false); }
            catch { try { context.Response.Abort(); } catch { } }
        }
    }

    private async Task HandleAgentAsync(HttpListenerContext context, byte[] body)
    {
        AgentPayload? payload;
        try { payload = JsonSerializer.Deserialize(body, WebhookJsonContext.Default.AgentPayload); }
        catch (JsonException) { await RespondAsync(context, 400, "invalid_json").ConfigureAwait(false); return; }
        if (payload is null || !payload.IsValid) { await RespondAsync(context, 400, "invalid_payload").ConfigureAwait(false); return; }
        PayloadReceived?.Invoke(this, payload with { ReceivedAt = DateTimeOffset.Now });
        await RespondAsync(context, 202, "{\"status\":\"accepted\"}").ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpListenerRequest request, int limit)
    {
        await using var stream = request.InputStream;
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > limit) return null;
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    /// <summary>The window of the terminal that ran the hook, so clicking its session focuses it.</summary>
    private (int? Pid, long? Hwnd) Sender(HttpListenerRequest request)
    {
        try { return request.RemoteEndPoint is { } remote ? Interop.ClientProcessResolver.Resolve(remote.Port, _port) : (null, null); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { return (null, null); }
    }

    private static async Task RespondAsync(HttpListenerContext context, int statusCode, string body, string contentType = "application/json")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
    }
}
