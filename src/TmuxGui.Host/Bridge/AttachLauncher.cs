using System.Diagnostics;
using TmuxGui.Host.Services;

namespace TmuxGui.Host.Bridge;

/// <summary>调起 Windows Terminal 执行 tmux attach（异步、不等待退出）。</summary>
public static class AttachLauncher
{
    public static Task LaunchAsync(string scope, string sessionName)
    {
        var name = Sanitize(sessionName);
        if (scope.StartsWith("ssh:", StringComparison.Ordinal))
        {
            var server = ConfigStore.Instance.GetServer(scope["ssh:".Length..])
                         ?? throw new TmuxRunnerException("找不到对应的 SSH 服务器配置。");
            var port = server.Port is > 0 ? server.Port.Value : 22;
            var user = string.IsNullOrWhiteSpace(server.Username) ? Environment.UserName : server.Username;
            // wt -w new ssh -t -p <port> user@host tmux attach -t <name>
            var wt = TmuxRunner.FindOnPath("wt.exe");
            if (wt is not null)
            {
                Start(wt, ["-w", "new", "ssh", "-t", "-p", port.ToString(), $"{user}@{server.Host}",
                    "tmux", "attach", "-t", name]);
                return Task.CompletedTask;
            }
            // fallback：cmd /c start，ssh -t 需要终端
            var cmd = TmuxRunner.FindOnPath("cmd.exe") ?? "cmd.exe";
            Start(cmd, ["/c", "start", "", "ssh", "-t", "-p", port.ToString(), $"{user}@{server.Host}",
                "tmux", "attach", "-t", name]);
            return Task.CompletedTask;
        }

        // 本地 / psmux：wt -w new tmux attach -t <name>
        var wtExe = TmuxRunner.FindOnPath("wt.exe");
        if (wtExe is not null)
        {
            Start(wtExe, ["-w", "new", "tmux", "attach", "-t", name]);
            return Task.CompletedTask;
        }
        // fallback：cmd /c start bash -lc "tmux attach -t <name>"
        var cmdExe = TmuxRunner.FindOnPath("cmd.exe") ?? "cmd.exe";
        Start(cmdExe, ["/c", "start", "", "bash", "-lc", $"tmux attach -t {name}"]);
        return Task.CompletedTask;
    }

    private static void Start(string fileName, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    /// <summary>会话名只用于命令行参数，剔除引号/控制符防注入。</summary>
    private static string Sanitize(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in name)
            if (!char.IsControl(c) && c is not ('\'' or '"' or '\\' or '`' or '$' or ';' or '|' or '&' or '>' or '<'))
                sb.Append(c);
        return sb.ToString();
    }
}
