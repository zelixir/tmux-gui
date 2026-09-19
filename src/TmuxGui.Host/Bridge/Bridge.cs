using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using TmuxGui.Host.Services;

namespace TmuxGui.Host.Bridge;

/// <summary>DTO：与前端交换的服务器/收藏 JSON 结构。</summary>
public sealed class ServerDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("port")] public JsonElement Port { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("authType")] public string AuthType { get; set; } = "key";
    [JsonPropertyName("keyPath")] public string? KeyPath { get; set; }
    [JsonPropertyName("lastError")] public string? LastError { get; set; }
}

public sealed class FavoriteDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("scope")] public string Scope { get; set; } = "local";
}

/// <summary>
/// 注册为 hostObject "bridge"。所有方法返回 JSON 字符串（camelCase）：
/// 成功 {"ok":true,...}，失败 {"ok":false,"error":"..."}。
/// 返回 Task&lt;string&gt;，WebView2 会自动在 JS 侧转成 Promise。
/// </summary>
[ComVisible(true)]
public class Bridge
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static string Ok(object? data = null) => JsonSerializer.Serialize(
        data is null ? new { ok = true } : data, JsonOpts);

    private static string Err(string message) => JsonSerializer.Serialize(new { ok = false, error = message }, JsonOpts);

    // ---------- 内部执行辅助 ----------

    private static async Task<TmuxResult> RunTmux(string scope, params string[] args)
    {
        var (exitCode, stdout, stderr) = await TmuxRunner.ExecuteTmuxAsync(scope, args);
        return new TmuxResult(exitCode, stdout, stderr);
    }

    private static string RequireNonEmpty(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new TmuxRunnerException($"参数 {name} 不能为空。")
            : value;

    // ---------- 会话 ----------

    public async Task<string> ListSessions(string scope)
    {
        try
        {
            var r = await RunTmux(scope, "list-sessions", "-F", TmuxRunner.SessionFormatRef);
            if (r.ExitCode != 0)
            {
                if (r.IsNoServerError)
                    return Ok(new { ok = true, sessions = Array.Empty<object>() });
                return Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr));
            }

            var host = HostFor(scope);
            var sessions = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split('\t'))
                .Where(f => f.Length >= 4 && f[0].Length > 0)
                .Select(f => new
                {
                    name = f[0],
                    created = FormatEpoch(f[1]),
                    windows = int.TryParse(f[2], out var w) ? w : 0,
                    attached = f[3] != "0",
                    host,
                })
                .ToList();
            return Ok(new { ok = true, sessions });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public Task<string> CapturePane(string scope, string session, int lines)
        // 三参兼容版，转调五参版（window/pane 为空 = 当前）
        => CapturePane(scope, session, "", "", lines);

    public async Task<string> CapturePane(string scope, string session, string window, string pane, int lines)
    {
        try
        {
            var s = RequireNonEmpty(session, "session");
            if (lines <= 0) lines = 2000;
            var target = string.IsNullOrWhiteSpace(window)
                ? s
                : string.IsNullOrWhiteSpace(pane)
                    ? $"{s}:{window}"
                    : $"{s}:{window}.{pane}";
            var r = await RunTmux(scope, "capture-pane", "-t", target, "-p", "-S", $"-{lines}");
            return r.ExitCode != 0
                ? Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr))
                : Ok(new { ok = true, output = r.Stdout });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> NewSession(string scope, string name, string command)
    {
        try
        {
            var n = RequireNonEmpty(name, "name");
            var args = command is { Length: > 0 }
                ? new[] { "new-session", "-d", "-s", n, command }
                : new[] { "new-session", "-d", "-s", n };
            var r = await RunTmux(scope, args);
            return r.ExitCode != 0 ? Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr)) : Ok();
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> KillSession(string scope, string name)
    {
        try
        {
            var r = await RunTmux(scope, "kill-session", "-t", RequireNonEmpty(name, "name"));
            return r.ExitCode != 0 ? Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr)) : Ok();
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> RenameSession(string scope, string oldName, string newName)
    {
        try
        {
            var r = await RunTmux(scope, "rename-session", "-t",
                RequireNonEmpty(oldName, "oldName"), RequireNonEmpty(newName, "newName"));
            return r.ExitCode != 0 ? Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr)) : Ok();
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> Attach(string scope, string name)
    {
        try
        {
            var n = RequireNonEmpty(name, "name");
            await AttachLauncher.LaunchAsync(scope, n);
            return Ok();
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // ---------- 窗口 ----------

    public async Task<string> ListWindows(string scope, string session)
    {
        try
        {
            var s = RequireNonEmpty(session, "session");
            var r = await RunTmux(scope, "list-windows", "-t", s, "-F", TmuxRunner.WindowFormatRef);
            if (r.ExitCode != 0)
            {
                if (r.IsNoServerError) return Ok(new { ok = true, windows = Array.Empty<object>() });
                return Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr));
            }
            var windows = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split('\t'))
                .Where(f => f.Length >= 5)
                .Select(f => new
                {
                    index = int.TryParse(f[0], out var i) ? i : 0,
                    name = f[1],
                    active = f[2] == "1",
                    panes = int.TryParse(f[3], out var p) ? p : 0,
                    layout = f[4],
                })
                .ToList();
            return Ok(new { ok = true, windows });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> NewWindow(string scope, string session, string name, string command)
    {
        try
        {
            var s = RequireNonEmpty(session, "session");
            var args = new List<string> { "new-window" };
            if (!string.IsNullOrWhiteSpace(name)) { args.Add("-n"); args.Add(name); }
            args.Add("-t"); args.Add(s);
            if (!string.IsNullOrWhiteSpace(command)) args.Add(command);
            var r = await RunTmux(scope, [.. args]);
            return r.ExitCode != 0 ? Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr)) : Ok();
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> RenameWindow(string scope, string session, string windowIndex, string newName)
    {
        try
        {
            var s = RequireNonEmpty(session, "session");
            var r = await RunTmux(scope, "rename-window", "-t", $"{s}:{windowIndex}",
                RequireNonEmpty(newName, "newName"));
            return r.ExitCode != 0 ? Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr)) : Ok();
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> KillWindow(string scope, string session, string windowIndex)
    {
        try
        {
            var s = RequireNonEmpty(session, "session");
            var r = await RunTmux(scope, "kill-window", "-t", $"{s}:{windowIndex}");
            return r.ExitCode != 0 ? Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr)) : Ok();
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> SelectWindow(string scope, string session, string windowIndex)
    {
        try
        {
            var s = RequireNonEmpty(session, "session");
            var r = await RunTmux(scope, "select-window", "-t", $"{s}:{windowIndex}");
            return r.ExitCode != 0 ? Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr)) : Ok();
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> NextWindow(string scope, string session)
    {
        try
        {
            var r = await RunTmux(scope, "next-window", "-t", RequireNonEmpty(session, "session"));
            return r.ExitCode != 0 ? Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr)) : Ok();
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> PreviousWindow(string scope, string session)
    {
        try
        {
            var r = await RunTmux(scope, "previous-window", "-t", RequireNonEmpty(session, "session"));
            return r.ExitCode != 0 ? Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr)) : Ok();
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> SwapWindow(string scope, string session, string srcIndex, string dstIndex)
    {
        try
        {
            var s = RequireNonEmpty(session, "session");
            var r = await RunTmux(scope, "swap-window", "-s", $"{s}:{srcIndex}", "-t", $"{s}:{dstIndex}");
            return r.ExitCode != 0 ? Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr)) : Ok();
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // ---------- 面板 ----------

    public async Task<string> ListPanes(string scope, string session, string window)
    {
        try
        {
            var s = RequireNonEmpty(session, "session");
            var target = string.IsNullOrWhiteSpace(window) ? s : $"{s}:{window}";
            var r = await RunTmux(scope, "list-panes", "-t", target, "-F", TmuxRunner.PaneFormatRef);
            if (r.ExitCode != 0)
            {
                if (r.IsNoServerError) return Ok(new { ok = true, panes = Array.Empty<object>() });
                return Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr));
            }
            var panes = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split('\t'))
                .Where(f => f.Length >= 8)
                .Select(f => new
                {
                    index = int.TryParse(f[0], out var i) ? i : 0,
                    active = f[1] == "1",
                    title = f[2],
                    cwd = f[3],
                    pid = int.TryParse(f[4], out var pid) ? pid : 0,
                    width = int.TryParse(f[5], out var w) ? w : 0,
                    height = int.TryParse(f[6], out var h) ? h : 0,
                    dead = f[7] == "1",
                })
                .ToList();
            return Ok(new { ok = true, panes });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> SplitPane(string scope, string session, string targetPane, string dir, string command)
    {
        try
        {
            var s = RequireNonEmpty(session, "session");
            var target = string.IsNullOrWhiteSpace(targetPane) ? s : $"{s}:{targetPane}";
            var args = new List<string> { "split-window" };
            args.Add(dir == "h" ? "-h" : "-v"); // h=左右，v=上下（默认 v）
            args.Add("-t"); args.Add(target);
            if (!string.IsNullOrWhiteSpace(command)) args.Add(command);
            var r = await RunTmux(scope, [.. args]);
            return r.ExitCode != 0 ? Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr)) : Ok();
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> KillPane(string scope, string session, string targetPane)
    {
        try
        {
            var s = RequireNonEmpty(session, "session");
            var target = string.IsNullOrWhiteSpace(targetPane) ? s : $"{s}:{targetPane}";
            var r = await RunTmux(scope, "kill-pane", "-t", target);
            return r.ExitCode != 0 ? Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr)) : Ok();
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> SelectPane(string scope, string session, string targetPane)
    {
        try
        {
            var s = RequireNonEmpty(session, "session");
            // targetPane 可为索引，也可为方向 L/R/U/D
            if (targetPane is "L" or "R" or "U" or "D")
            {
                var r = await RunTmux(scope, "select-pane", "-t", s, $"-{targetPane}");
                return r.ExitCode != 0 ? Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr)) : Ok();
            }
            var target = string.IsNullOrWhiteSpace(targetPane) ? s : $"{s}:{targetPane}";
            var r2 = await RunTmux(scope, "select-pane", "-t", target);
            return r2.ExitCode != 0 ? Err(TmuxRunner.FriendlyError(r2.ExitCode, r2.Stderr)) : Ok();
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> ResizePane(string scope, string session, string targetPane, string dir, int cells)
    {
        try
        {
            var s = RequireNonEmpty(session, "session");
            if (dir is not ("L" or "R" or "U" or "D"))
                return Err("参数 dir 必须为 L/R/U/D 之一。");
            if (cells <= 0) cells = 1;
            var target = string.IsNullOrWhiteSpace(targetPane) ? s : $"{s}:{targetPane}";
            var r = await RunTmux(scope, "resize-pane", "-t", target, $"-{dir}", cells.ToString());
            return r.ExitCode != 0 ? Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr)) : Ok();
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> RotatePane(string scope, string session, string windowIndex, string dir)
    {
        try
        {
            var s = RequireNonEmpty(session, "session");
            if (dir is not ("f" or "b"))
                return Err("参数 dir 必须为 f(forward)/b(backward) 之一。");
            var r = await RunTmux(scope, "rotate-window", "-t", $"{s}:{windowIndex}", dir == "f" ? "-D" : "-U");
            return r.ExitCode != 0 ? Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr)) : Ok();
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> SelectLayout(string scope, string session, string windowIndex, string layout)
    {
        try
        {
            var s = RequireNonEmpty(session, "session");
            var valid = new[] { "even-horizontal", "even-vertical", "main-pane", "tiled" };
            if (!valid.Contains(layout))
                return Err($"layout 必须为 {string.Join(" | ", valid)} 之一。");
            var r = await RunTmux(scope, "select-layout", "-t", $"{s}:{windowIndex}", layout);
            return r.ExitCode != 0 ? Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr)) : Ok();
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // ---------- 交互与命令 ----------

    public async Task<string> SendKeys(string scope, string session, string targetPane, string keys, bool enter)
    {
        try
        {
            var s = RequireNonEmpty(session, "session");
            var target = string.IsNullOrWhiteSpace(targetPane) ? s : $"{s}:{targetPane}";
            var args = new List<string> { "send-keys", "-t", target };
            if (!string.IsNullOrEmpty(keys)) args.Add(keys); // tmux 特殊键名（C-c 等）透传
            if (enter) args.Add("Enter");
            var r = await RunTmux(scope, [.. args]);
            return r.ExitCode != 0 ? Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr)) : Ok();
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> RunTmuxCommand(string scope, string args)
    {
        try
        {
            var tokens = Tokenize(args);
            if (tokens.Length == 0) return Err("命令不能为空。");
            var r = await RunTmux(scope, tokens);
            var output = (r.Stdout + r.Stderr).TrimEnd();
            return r.ExitCode != 0
                ? Err(TmuxRunner.FriendlyError(r.ExitCode, output))
                : Ok(new { ok = true, output });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> ShowOptions(string scope, string session)
    {
        try
        {
            var rSession = string.IsNullOrWhiteSpace(session)
                ? await RunTmux(scope, "show-options")
                : await RunTmux(scope, "show-options", "-t", session);
            var rGlobal = await RunTmux(scope, "show-options", "-g");
            var text = string.Join("\n",
                new[] { rSession.Stdout.TrimEnd(), rGlobal.Stdout.TrimEnd() }.Where(s => s.Length > 0));
            if (rSession.ExitCode != 0 && !rSession.IsNoServerError)
                return Err(TmuxRunner.FriendlyError(rSession.ExitCode, rSession.Stderr));
            return Ok(new { ok = true, output = text });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> SetOption(string scope, string session, string option, string value)
    {
        try
        {
            var opt = RequireNonEmpty(option, "option");
            var args = string.IsNullOrWhiteSpace(session)
                ? new[] { "set-option", opt, value }
                : new[] { "set-option", "-t", session, opt, value };
            var r = await RunTmux(scope, args);
            return r.ExitCode != 0 ? Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr)) : Ok();
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    public async Task<string> ListKeys(string scope)
    {
        try
        {
            var r = await RunTmux(scope, "list-keys");
            return r.ExitCode != 0
                ? Err(TmuxRunner.FriendlyError(r.ExitCode, r.Stderr))
                : Ok(new { ok = true, output = r.Stdout });
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // ---------- 服务器配置 ----------

    public Task<string> ListServers()
    {
        try
        {
            var servers = ConfigStore.Instance.GetServers()
                .Select(s => new
                {
                    id = s.Id, name = s.Name, host = s.Host, port = s.Port ?? 22,
                    username = s.Username, authType = s.AuthType, keyPath = s.KeyPath, lastError = s.LastError,
                })
                .ToList();
            return Task.FromResult(Ok(new { ok = true, servers }));
        }
        catch (Exception ex) { return Task.FromResult(Err(ex.Message)); }
    }

    public Task<string> SaveServer(string json)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<ServerDto>(json, JsonOpts)
                      ?? throw new TmuxRunnerException("服务器 JSON 无效。");
            if (string.IsNullOrWhiteSpace(dto.Host))
                throw new TmuxRunnerException("服务器 host 不能为空。");
            int? port = dto.Port.ValueKind == JsonValueKind.Number && dto.Port.TryGetInt32(out var p)
                ? p
                : null;
            var id = ConfigStore.Instance.UpsertServer(new ServerConfig
            {
                Id = dto.Id,
                Name = string.IsNullOrWhiteSpace(dto.Name) ? dto.Host : dto.Name,
                Host = dto.Host.Trim(),
                Port = port,
                Username = dto.Username,
                AuthType = string.IsNullOrWhiteSpace(dto.AuthType) ? "key" : dto.AuthType,
                KeyPath = dto.KeyPath,
            });
            return Task.FromResult(Ok(new { ok = true, id }));
        }
        catch (JsonException ex) { return Task.FromResult(Err("JSON 解析失败：" + ex.Message)); }
        catch (Exception ex) { return Task.FromResult(Err(ex.Message)); }
    }

    public Task<string> DeleteServer(string id)
    {
        try
        {
            return Task.FromResult(ConfigStore.Instance.DeleteServer(id) ? Ok() : Err($"找不到服务器 {id}。"));
        }
        catch (Exception ex) { return Task.FromResult(Err(ex.Message)); }
    }

    public async Task<string> TestServer(string id)
    {
        try
        {
            var r = await RunTmux($"ssh:{id}", "list-sessions");
            if (r.ExitCode == 0 || r.IsNoServerError)
            {
                ConfigStore.Instance.SetServerError(id, null);
                return Ok();
            }
            var error = TmuxRunner.FriendlyError(r.ExitCode, r.Stderr);
            ConfigStore.Instance.SetServerError(id, error);
            return Ok(new { ok = true, error }); // 契约：失败时 data.error（仍 ok:true 便于前端区分网络失败与调用异常）
        }
        catch (Exception ex) { return Err(ex.Message); }
    }

    // ---------- 收藏 ----------

    public Task<string> ListFavorites()
    {
        try
        {
            var favorites = ConfigStore.Instance.GetFavorites()
                .Select(f => new { id = f.Id, name = f.Name, command = f.Command, scope = f.Scope })
                .ToList();
            return Task.FromResult(Ok(new { ok = true, favorites }));
        }
        catch (Exception ex) { return Task.FromResult(Err(ex.Message)); }
    }

    public Task<string> SaveFavorite(string json)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<FavoriteDto>(json, JsonOpts)
                      ?? throw new TmuxRunnerException("收藏 JSON 无效。");
            if (string.IsNullOrWhiteSpace(dto.Command))
                throw new TmuxRunnerException("收藏 command 不能为空。");
            var id = ConfigStore.Instance.UpsertFavorite(new FavoriteConfig
            {
                Id = dto.Id,
                Name = string.IsNullOrWhiteSpace(dto.Name) ? dto.Command : dto.Name,
                Command = dto.Command,
                Scope = string.IsNullOrWhiteSpace(dto.Scope) ? "local" : dto.Scope,
            });
            return Task.FromResult(Ok(new { ok = true, id }));
        }
        catch (JsonException ex) { return Task.FromResult(Err("JSON 解析失败：" + ex.Message)); }
        catch (Exception ex) { return Task.FromResult(Err(ex.Message)); }
    }

    public Task<string> DeleteFavorite(string id)
    {
        try
        {
            return Task.FromResult(ConfigStore.Instance.DeleteFavorite(id) ? Ok() : Err($"找不到收藏 {id}。"));
        }
        catch (Exception ex) { return Task.FromResult(Err(ex.Message)); }
    }

    // ---------- 工具 ----------

    private string HostFor(string scope) =>
        scope.StartsWith("ssh:", StringComparison.Ordinal)
            ? ConfigStore.Instance.GetServer(scope["ssh:".Length..])?.Host ?? scope
            : "localhost";

    private static string FormatEpoch(string epoch)
    {
        if (!long.TryParse(epoch, out var seconds) || seconds <= 0) return epoch;
        return DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
    }

    /// <summary>简单的 shell 风格分词（支持单/双引号），用于 RunTmuxCommand 透传。</summary>
    private static string[] Tokenize(string input)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var hasToken = false;
        char? quote = null;
        foreach (var c in input ?? "")
        {
            switch (quote)
            {
                case '\'' when c != '\'': current.Append(c); break;
                case '\'' : quote = null; break;
                case '"' when c != '"': current.Append(c); break;
                case '"': quote = null; break;
                default:
                    if (c is '\'' or '"') { quote = c; hasToken = true; }
                    else if (char.IsWhiteSpace(c))
                    {
                        if (hasToken || current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); hasToken = false; }
                    }
                    else { current.Append(c); hasToken = true; }
                    break;
            }
        }
        if (hasToken || current.Length > 0) tokens.Add(current.ToString());
        return [.. tokens];
    }
}
