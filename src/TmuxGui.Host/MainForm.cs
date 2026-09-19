using Microsoft.Web.WebView2.WinForms;
using TmuxGui.Host.Bridge;

namespace TmuxGui.Host;

/// <summary>主窗体：内嵌 WebView2，加载 wwwroot/index.html 并注册 hostObject "bridge"。</summary>
public sealed class MainForm : Form
{
    private readonly WebView2 _webView;

    public MainForm()
    {
        Text = "tmux-gui";
        Width = 1280;
        Height = 800;
        StartPosition = FormStartPosition.CenterScreen;

        _webView = new WebView2 { Dock = DockStyle.None, Bounds = ClientRectangle, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
        Controls.Add(_webView);

        Load += async (_, _) => await InitWebViewAsync();
    }

    private static string WwwRootPath()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task InitWebViewAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "tmux-gui", "WebView2");
        var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
            userDataFolder: userDataFolder);
        await _webView.EnsureCoreWebView2Async(environment);

        var core = _webView.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(
            "app.local", WwwRootPath(),
            Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
        core.AddHostObjectToScript("bridge", new global::TmuxGui.Host.Bridge.Bridge());
        core.Navigate("https://app.local/index.html");
    }
}
