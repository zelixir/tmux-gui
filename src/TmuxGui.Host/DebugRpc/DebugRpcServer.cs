using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BridgeClass = TmuxGui.Host.Bridge.Bridge;

namespace TmuxGui.Host.DebugRpc;

/// <summary>
/// --debug 开关启动的本地 JSON-RPC 调试服务（仅绑定 127.0.0.1）。
/// POST /rpc：{"method":"<Bridge方法名>","params":[...]} → 反射调用 Bridge，返回 JSON 原样。
/// GET /methods：可调用方法名数组。
/// 所有错误在 body 内表达（HTTP 一律 200）。
/// </summary>
public sealed class DebugRpcServer
{
    private readonly HttpListener _listener = new();
    private readonly BridgeClass _bridge;
    private readonly Thread _acceptThread;
    private readonly object _logLock = new();
    private readonly string _logPath;
    private volatile bool _running;

    /// <summary>实际监听的端口（构造时传入）。</summary>
    public int Port { get; }

    private static readonly Dictionary<Type, Func<JsonElement, object>> Converters = new()
    {
        [typeof(string)] = e => e.ToString(),
        [typeof(int)] = e => e.ValueKind == JsonValueKind.String
            ? int.Parse(e.GetString()!)
            : e.GetInt32(),
        [typeof(bool)] = e => e.ValueKind == JsonValueKind.String
            ? bool.Parse(e.GetString()!)
            : e.GetBoolean(),
    };

    public DebugRpcServer(BridgeClass bridge, int port)
    {
        _bridge = bridge;
        Port = port;
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "DebugRpcServer" };
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "tmux-gui", "debug.log");
    }

    /// <summary>启动监听。失败（端口占用等）抛异常，由调用方决定如何报告，不阻塞 GUI。</summary>
    public void Start()
    {
        _listener.Start();
        _running = true;
        _acceptThread.Start();
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch (Exception) when (!_running) { return; }
            catch (HttpListenerException) { if (_running) continue; return; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var response = ctx.Request.HttpMethod switch
            {
                "POST" when ctx.Request.Url?.AbsolutePath == "/rpc" => await HandleRpcAsync(ctx.Request),
                "GET" when ctx.Request.Url?.AbsolutePath == "/methods" => HandleMethods(),
                _ => Error("not found: use POST /rpc or GET /methods"),
            };
            WriteResponse(ctx, response);
        }
        catch (Exception ex)
        {
            try { WriteResponse(ctx, Error(ex.Message)); } catch { /* ignore */ }
        }
    }

    private async Task<string> HandleRpcAsync(HttpListenerRequest request)
    {
        string body;
        using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
            body = await reader.ReadToEndAsync();

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return Error("invalid JSON: " + ex.Message);
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("method", out var methodEl)
            || methodEl.ValueKind != JsonValueKind.String)
            return Error("missing string field: method");

        var method = methodEl.GetString()!;
        var paramsEl = root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Array
            ? p
            : (JsonElement?)null;

        var sw = Stopwatch.StartNew();
        string result;
        try { result = Invoke(method, paramsEl); }
        catch (Exception ex) { result = Error(ex.Message); }
        sw.Stop();
        Log(method, sw.ElapsedMilliseconds);
        return result;
    }

    /// <summary>按名称+参数个数匹配 Bridge 公开契约方法并调用。</summary>
    private string Invoke(string method, JsonElement? paramsEl)
    {
        var args = paramsEl?.EnumerateArray().ToList() ?? [];

        var candidates = typeof(BridgeClass)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.DeclaringType == typeof(BridgeClass)
                && m.ReturnType == typeof(Task<string>)
                && m.Name == method)
            .ToList();

        var target = candidates.FirstOrDefault(m => m.GetParameters().Length == args.Count)
            ?? candidates.FirstOrDefault();
        if (target is null)
            throw new InvalidOperationException($"unknown method: {method}");

        var parameters = target.GetParameters();
        if (parameters.Length != args.Count)
            throw new InvalidOperationException(
                $"parameter count mismatch for {method}: expected {parameters.Length}, got {args.Count}");

        var converted = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var t = parameters[i].ParameterType;
            if (!Converters.TryGetValue(t, out var conv))
                throw new InvalidOperationException($"unsupported parameter type: {t.Name}");
            try { converted[i] = conv(args[i]); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"failed to convert parameter '{parameters[i].Name}' to {t.Name}: {ex.Message}");
            }
        }

        var task = (Task<string>)target.Invoke(_bridge, converted)!;
        return task.GetAwaiter().GetResult();
    }

    private string HandleMethods() => JsonSerializer.Serialize(
        typeof(BridgeClass)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.DeclaringType == typeof(BridgeClass) && m.ReturnType == typeof(Task<string>))
            .Select(m => m.Name)
            .Distinct()
            .OrderBy(n => n)
            .ToList());

    private static string Error(string message) =>
        JsonSerializer.Serialize(new { ok = false, error = message });

    private static void WriteResponse(HttpListenerContext ctx, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private void Log(string method, long elapsedMs)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            lock (_logLock)
                File.AppendAllText(_logPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\t{method}\t{elapsedMs}ms{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响 RPC
        }
    }
}
