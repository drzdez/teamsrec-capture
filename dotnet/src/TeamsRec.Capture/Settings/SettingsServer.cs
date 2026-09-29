using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using TeamsRec.Capture.App;
using TeamsRec.Capture.Audio;
using TeamsRec.Capture.Config;
using TeamsRec.Capture.Core;

namespace TeamsRec.Capture.Settings;

/// <summary>The settings page (browser, 127.0.0.1 only). The page is one embedded file; everything else is a
/// small JSON API, the same as the prototype's, so settings.html works unchanged against both.</summary>
public sealed class SettingsServer : IDisposable
{
    private readonly AppConfig _cfg;
    private readonly Func<bool> _recording;
    private HttpListener? _listener;

    // ensure_ascii=False: Czech device names and messages stay readable in the JSON
    internal static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public SettingsServer(AppConfig cfg, Func<bool> recording)
    {
        _cfg = cfg;
        _recording = recording;
    }

    /// <summary>The page's address once started.</summary>
    public string? Url { get; private set; }

    /// <summary>Start on first use (a listener nobody needs is not left running) and return the page's address.</summary>
    public string Start()
    {
        if (Url is not null) return Url;
        int port = FreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            listener.Start();
            Url = $"http://127.0.0.1:{port}/";
        }
        catch (HttpListenerException)
        {
            // http.sys may refuse an explicit IP prefix without a URL reservation; "localhost" is always
            // allowed for a normal user and is still loopback only
            listener.Close();
            listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
            Url = $"http://localhost:{port}/";
        }
        _listener = listener;
        Log.Info($"settings page on {Url}");
        _ = Task.Run(AcceptLoop);
        return Url;
    }

    /// <summary>Start (if needed) and open the page in the default browser.</summary>
    public void Open() =>
        Process.Start(new ProcessStartInfo(Start()) { UseShellExecute = true });

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    private async Task AcceptLoop()
    {
        var listener = _listener;
        while (listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;  // stopped
            }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var path = req.Url?.AbsolutePath ?? "/";
            if (req.HttpMethod == "GET")
            {
                if (path.StartsWith("/api/settings", StringComparison.Ordinal))
                    Reply(ctx, State());
                else if (path is "/" or "/index.html" or "/settings.html")
                    Page(ctx);
                else
                    Status(ctx, 404);
                return;
            }
            if (req.HttpMethod != "POST")
            {
                Status(ctx, 405);
                return;
            }
            // Only the page itself may change settings: a web page in another tab could otherwise POST to
            // 127.0.0.1 (a "simple" cross-origin request needs no preflight).
            if (!SameOrigin(req.Headers["Origin"], Url))
            {
                Reply(ctx, new Dictionary<string, object?> { ["error"] = "cizí původ požadavku" }, 403);
                return;
            }
            JsonObject data;
            try
            {
                using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                var body = reader.ReadToEnd();
                data = JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body) as JsonObject
                       ?? throw new JsonException("not an object");
            }
            catch (JsonException)
            {
                Reply(ctx, new Dictionary<string, object?> { ["error"] = "nečitelný požadavek" }, 400);
                return;
            }
            switch (path)
            {
                case "/api/settings":
                    try
                    {
                        var values = data["values"] as JsonObject ?? new JsonObject();
                        var applied = SettingsModel.Save(ConfigPath, _cfg, values);
                        Reply(ctx, new Dictionary<string, object?> { ["ok"] = true, ["applied"] = applied });
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException
                                                  or IOException or UnauthorizedAccessException)
                    {
                        Reply(ctx, new Dictionary<string, object?> { ["error"] = $"uložení selhalo: {e.Message}" }, 400);
                    }
                    return;
                case "/api/test":
                    if (_recording())
                    {
                        Reply(ctx, new Dictionary<string, object?>
                        {
                            ["error"] = "právě běží nahrávání, test mikrofonu teď nejde",
                        });
                        return;
                    }
                    var device = data["device"] is JsonValue dv && dv.TryGetValue<string>(out var ds) ? ds : "";
                    Reply(ctx, TestResult(device));
                    return;
                case "/api/sound-panel":
                    Process.Start(new ProcessStartInfo("control", "mmsys.cpl") { UseShellExecute = true });
                    Reply(ctx, new Dictionary<string, object?> { ["ok"] = true });
                    return;
                default:
                    Status(ctx, 404);
                    return;
            }
        }
        catch (Exception e)
        {
            FileLog.Exception("settings request", e);
            try { Reply(ctx, new Dictionary<string, object?> { ["error"] = e.Message }, 500); }
            catch (Exception) { /* the browser went away */ }
        }
    }

    /// <summary>A POST without an Origin header (curl, tests) or from the page itself is accepted.</summary>
    internal static bool SameOrigin(string? origin, string? url)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        if (url is null) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var o) || !Uri.TryCreate(url, UriKind.Absolute, out var u))
            return false;
        bool loopbackHost(string h) => h is "127.0.0.1" or "localhost";
        return o.Scheme == u.Scheme && o.Port == u.Port && loopbackHost(o.Host) && loopbackHost(u.Host);
    }

    /// <summary>The TOML the config was loaded from, the one the page edits.</summary>
    private string ConfigPath => string.IsNullOrEmpty(_cfg.SourcePath) ? AppConfig.DefaultPath : _cfg.SourcePath;

    /// <summary>GET /api/settings.</summary>
    private Dictionary<string, object?> State() => new()
    {
        ["version"] = Versions.AppVersion,
        ["config_path"] = ConfigPath,
        ["out_dir"] = _cfg.OutDir,
        ["recording"] = _recording(),
        ["values"] = SettingsModel.Values(_cfg),
        ["devices"] = DevicesJson(SafeList(() => Devices.Inputs(), "input device list")),
        ["unavailable"] = UnavailableJson(SafeList(() => Devices.Unavailable(), "unavailable device list")),
    };

    private static IReadOnlyList<T> SafeList<T>(Func<IEnumerable<T>> get, string what)
    {
        try { return get().ToList(); }
        catch (Exception e)
        {
            FileLog.Exception(what, e);
            return [];
        }
    }

    internal static List<Dictionary<string, object?>> DevicesJson(IEnumerable<InputDevice> devices) =>
        devices.Select(d => new Dictionary<string, object?>
        {
            ["name"] = d.Name, ["channels"] = d.Channels, ["rate"] = d.SampleRate, ["is_default"] = d.IsDefault,
        }).ToList();

    internal static List<Dictionary<string, object?>> UnavailableJson(IEnumerable<UnavailableInput> items) =>
        items.Select(u => new Dictionary<string, object?>
        {
            ["name"] = u.Name, ["device"] = u.Device, ["reason"] = u.Reason,
        }).ToList();

    /// <summary>POST /api/test: {device, peak, seconds, silence_level} or {error}.</summary>
    private static Dictionary<string, object?> TestResult(string device)
    {
        const double seconds = 2;
        var r = MicTest.Run(device, seconds);
        if (!string.IsNullOrEmpty(r.Error))
            return new() { ["error"] = r.Error };
        return new()
        {
            ["device"] = r.Device, ["peak"] = r.Peak, ["seconds"] = seconds, ["silence_level"] = Recorder.SilenceLevel,
        };
    }

    private static void Page(HttpListenerContext ctx)
    {
        using var s = typeof(SettingsServer).Assembly.GetManifestResourceStream("settings.html");
        if (s is null)
        {
            Reply(ctx, new Dictionary<string, object?> { ["error"] = "settings.html: missing resource" }, 500);
            return;
        }
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        Write(ctx, 200, "text/html; charset=utf-8", ms.ToArray());
    }

    private static void Reply(HttpListenerContext ctx, object obj, int code = 200) =>
        Write(ctx, code, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(obj, Json));

    private static void Status(HttpListenerContext ctx, int code)
    {
        ctx.Response.StatusCode = code;
        ctx.Response.Close();
    }

    private static void Write(HttpListenerContext ctx, int code, string contentType, byte[] body)
    {
        var resp = ctx.Response;
        resp.StatusCode = code;
        resp.ContentType = contentType;
        resp.ContentLength64 = body.Length;
        resp.Headers["Cache-Control"] = "no-store";
        resp.OutputStream.Write(body, 0, body.Length);
        resp.Close();
    }

    public void Dispose()
    {
        try { _listener?.Stop(); _listener?.Close(); }
        catch (Exception) { }
        _listener = null;
    }
}
