using System.Diagnostics;

namespace TmuxGui.Host.Services;

/// <summary>tmux 执行结果。</summary>
public sealed record TmuxResult(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>exit 1 且 stderr 为"无服务器/连不上"类信息时视为空列表而非错误。</summary>
    public bool IsNoServerError =>
        ExitCode == 1 &&
        (Stderr.Contains("no server", StringComparison.OrdinalIgnoreCase) ||
         Stderr.Contains("no sockets", StringComparison.OrdinalIgnoreCase) ||
         Stderr.Contains("error connecting", StringComparison.OrdinalIgnoreCase));
}

/// <summary>执行环境类失败（找不到 tmux/ssh、超时、认证失败等），Bridge 直接把 Message 返回给前端。</summary>
public sealed class TmuxRunnerException(string message) : Exception(message);

/// <summary>
/// tmux 执行抽象。scope："local" | "psmux" | "ssh:&lt;serverId&gt;"。
/// 始终异步读取 stdout/stderr，防止并发 CapturePane 轮询时管道缓冲区塞满导致死锁。
/// </summary>
public static class TmuxRunner
{
    public const int TimeoutMs = 30_000;

    private const string SessionFormat =
        "#{session_name}\t#{session_created}\t#{session_windows}\t#{session_attached}";
    private const string WindowFormat =
        "#{window_index}\t#{window_name}\t#{window_active}\t#{window_panes}\t#{window_layout}";
    private const string PaneFormat =
        "#{pane_index}\t#{pane_active}\t#{pane_title}\t#{pane_current_path}\t#{pane_pid}\t#{pane_width}\t#{pane_height}\t#{pane_dead}";

    public static string SessionFormatRef => SessionFormat;
    public static string WindowFormatRef => WindowFormat;
    public static string PaneFormatRef => PaneFormat;

    /// <summary>在指定 scope 下执行 tmux 命令，返回 (exitCode, stdout, stderr)。</summary>
    public static async Task<(int exitCode, string stdout, string stderr)> ExecuteTmuxAsync(
        string scope, params string[] tmuxArgs)
    {
        scope = (scope ?? "local").Trim();
        if (scope.Equals("local", StringComparison.OrdinalIgnoreCase))
            return await ExecuteAsync(BuildLocalPsi(FindTmuxOrThrow(), tmuxArgs));
        if (scope.Equals("psmux", StringComparison.OrdinalIgnoreCase))
            return await ExecuteAsync(BuildPsmuxPsi(tmuxArgs));
        if (scope.StartsWith("ssh:", StringComparison.Ordinal))
            return await ExecuteAsync(BuildSshPsi(scope["ssh:".Length..], tmuxArgs));
        throw new TmuxRunnerException($"未知的 scope：{scope}");
    }

    // ---------- local ----------

    private static ProcessStartInfo BuildLocalPsi(string tmuxPath, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = tmuxPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static string FindTmuxOrThrow()
    {
        var tmux = FindOnPath("tmux");
        if (tmux is null)
            throw new TmuxRunnerException(
                "未在本机 PATH 中找到 tmux。请安装 tmux 并加入 PATH（可选：WSL 内的 tmux、Git Bash / MSYS2 自带 tmux、scoop install tmux 等），或改用 psmux / SSH 服务器 scope。");
        return tmux;
    }

    // ---------- psmux ----------

    private static ProcessStartInfo BuildPsmuxPsi(string[] args)
    {
        var ps = FindOnPath("powershell.exe")
                 ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                     "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(ps))
            throw new TmuxRunnerException("未找到 powershell.exe，无法执行 psmux。");

        // psmux 子命令按 tmux 兼容语义直接透传（psmux 兼容 tmux 命令行）
        var command = "psmux " + string.Join(" ", args.Select(PsSingleQuote));
        var psi = new ProcessStartInfo
        {
            FileName = ps,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);
        return psi;
    }

    private static string PsSingleQuote(string arg) => "'" + arg.Replace("'", "''") + "'";

    // ---------- ssh ----------

    private static ProcessStartInfo BuildSshPsi(string serverId, string[] args)
    {
        var server = ConfigStore.Instance.GetServer(serverId)
                     ?? throw new TmuxRunnerException($"找不到 id 为 {serverId} 的 SSH 服务器配置。");

        var ssh = FindOnPath("ssh.exe");
        var sysSsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "OpenSSH", "ssh.exe");
        if (File.Exists(sysSsh)) ssh = sysSsh;
        if (ssh is null)
            throw new TmuxRunnerException("未找到 ssh.exe（系统 OpenSSH）。请确认 Windows 可选功能 OpenSSH 客户端已安装。");

        var port = server.Port is > 0 ? server.Port.Value : 22;
        var user = string.IsNullOrWhiteSpace(server.Username) ? Environment.UserName : server.Username;
        var remote = "tmux " + string.Join(" ", args.Select(ShSingleQuote));

        var psi = new ProcessStartInfo
        {
            FileName = ssh,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("BatchMode=yes");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("StrictHostKeyChecking=accept-new");
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add($"{user}@{server.Host}");
        psi.ArgumentList.Add(remote);
        return psi;
    }

    /// <summary>POSIX shell 单引号转义：' -> '\''</summary>
    private static string ShSingleQuote(string arg) => "'" + arg.Replace("'", "'\\''") + "'";

    // ---------- 通用执行 ----------

    private static async Task<(int exitCode, string stdout, string stderr)> ExecuteAsync(ProcessStartInfo psi)
    {
        using var process = new Process { StartInfo = psi };
        process.Start();

        // 异步并发读取两个流，避免管道缓冲区写满阻塞子进程（并发 CapturePane 轮询场景）
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = new CancellationTokenSource(TimeoutMs);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try { await Task.WhenAll(stdoutTask, stderrTask); }
            catch { /* 忽略读取异常，超时才是根因 */ }
            throw new TmuxRunnerException($"执行超时（{TimeoutMs / 1000} 秒）：{psi.FileName}");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* 进程可能已退出 */ }
    }

    /// <summary>在 PATH（含 Git Bash 常见目录）中查找可执行文件。</summary>
    public static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var rawDir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = rawDir.Trim('"');
            if (dir.Length == 0) continue;
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) return candidate;
                if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(candidate + ".exe")) return candidate + ".exe";
            }
            catch { /* 目录名可能含非法字符，跳过 */ }
        }
        return null;
    }

    /// <summary>把 stderr 映射为友好错误信息（含 SSH 认证失败、psmux 缺失等）。</summary>
    public static string FriendlyError(int exitCode, string stderr)
    {
        if (exitCode == -1) return stderr;
        if (stderr.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase))
            return "SSH 认证失败：本应用使用系统 ssh 的密钥/agent 认证（BatchMode）。请为本服务器配置密钥认证（ssh-keygen + ssh-copy-id），密码认证不可用。详情：" + stderr.Trim();
        if (stderr.Contains("not recognized", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("无法识别", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("command not found", StringComparison.OrdinalIgnoreCase))
            return "未找到 psmux 命令：请在 PowerShell 环境中安装 psmux 模块后重试。详情：" + stderr.Trim();
        return stderr.Trim().Length > 0 ? stderr.Trim() : $"tmux 退出码 {exitCode}";
    }
}
