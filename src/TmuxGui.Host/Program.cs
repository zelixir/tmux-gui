using BridgeClass = TmuxGui.Host.Bridge.Bridge;
using TmuxGui.Host.Bridge;
using TmuxGui.Host.DebugRpc;

namespace TmuxGui.Host;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var (debugPort, error) = TryParseDebugPort(args);
        if (error is not null)
        {
            MessageBox.Show(error, "tmux-gui 启动参数错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ApplicationConfiguration.Initialize();

        // bridge 提前创建，供 GUI 与 Debug RPC 共用同一实例。
        var bridge = new BridgeClass();

        DebugRpcServer? debugServer = null;
        if (debugPort is { } port)
        {
            try
            {
                debugServer = new DebugRpcServer(bridge, port);
                debugServer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Debug RPC 服务启动失败（端口 {port}）：{ex.Message}",
                    "tmux-gui --debug", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                debugServer = null;
            }
        }

        Application.Run(new MainForm(bridge, debugServer is null ? null : debugServer.Port));
    }

    /// <summary>解析 --debug [port]（默认 8765）。返回解析错误信息（若有）。</summary>
    private static (int? Port, string? Error) TryParseDebugPort(string[] args)
    {
        var index = Array.FindIndex(args, a => a is "--debug" or "-d");
        if (index < 0) return (null, null);

        var port = 8765;
        if (index + 1 < args.Length && int.TryParse(args[index + 1], out var p))
        {
            port = p;
        }
        else if (index + 1 < args.Length && !args[index + 1].StartsWith('-'))
        {
            return (null, $"无效的 debug 端口：{args[index + 1]}");
        }

        if (port is < 1 or > 65535)
            return (null, $"debug 端口超出范围（1-65535）：{port}");
        return (port, null);
    }
}
