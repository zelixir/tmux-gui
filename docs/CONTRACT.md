# tmux-gui 架构契约（各子 agent 必须严格遵守）

## 技术栈
- net8.0-windows，WinForms 宿主 + `Microsoft.Web.WebView2`（NuGet 包 `Microsoft.Web.WebView2.Embedded` 或 `...Frameworks`，任选，需自包含运行）
- JSON 序列化：`System.Text.Json`（全局配置 `JsonSerializerOptions`：camelCase、IncludeFields=false）
- SSH：`系统自带 OpenSSH（`ssh.exe`，PATH 中调用；认证依赖密钥/agent，密码认证场景在 UI 提示改用密钥）
- 前端：纯 HTML/CSS/JS 单页（wwwroot/index.html），无构建步骤，无外部 CDN 依赖

## 文件域（互不越界）
- 后端 agent：`src/TmuxGui.Host/**` 中除 `wwwroot/**` 外的所有文件
- 前端 agent：`src/TmuxGui.Host/wwwroot/**`
- 主 agent：sln、csproj、`App.xaml`级入口（Program.cs）、收口

## JS 桥接契约
C# 侧注册 hostObject 名为 `bridge`，前端通过
`window.chrome.webview.hostObjects.bridge.<Method>(...)` 调用。
**所有方法均返回 JSON 字符串**（前端统一 `JSON.parse`）；抛错时返回
`{"ok":false,"error":"<message>"}`，成功时 `{"ok":true, ...data}`。

接口（方法名精确匹配，参数为字符串）：

```
ListSessions(string scope)            // scope: "local" | "psmux" | "ssh:<serverId>"
  -> data.sessions: [{name, created, windows, attached, host}]
CapturePane(string scope, string session, int lines)  // lines<=0 表示 2000
  -> data.output: string        // tmux capture-pane -p -S -N 纯文本
NewSession(string scope, string name, string command) // command 可为空
KillSession(string scope, string name)
RenameSession(string scope, string oldName, string newName)
Attach(string scope, string name)     // 调起 Windows Terminal（wt），异步，立即返回
KillOtherSessions(string scope, string keepName)  // tmux kill-server 外的批量清理可由前端循环实现，可不实现

## 窗口/面板（完整对象模型，全部必做）
ListWindows(string scope, string session)
  -> data.windows: [{index, name, active, panes, layout}]
ListPanes(string scope, string session, string window)
  -> data.panes: [{index, active, title, cwd, pid, width, height, dead}]
NewWindow(string scope, string session, string name, string command)
RenameWindow(string scope, string session, string windowIndex, string newName)
KillWindow(string scope, string session, string windowIndex)
SelectWindow(string scope, string session, string windowIndex)
SplitPane(string scope, string session, string targetPane, string dir, string command)
  // dir: "h"(-h 左右) | "v"(-v 上下)
KillPane(string scope, string session, string targetPane)
SelectPane(string scope, string session, string targetPane)          // targetPane 可为索引或方向 "L R U D"
ResizePane(string scope, string session, string targetPane, string dir, int cells) // dir: L R U D
RotatePane(string scope, string session, string windowIndex, string dir)           // dir: "f"(forward) | "b"(backward)
SelectLayout(string scope, string session, string windowIndex, string layout)
  // layout: even-horizontal | even-vertical | main-pane | tiled
NextWindow(string scope, string session)  /  PreviousWindow(string scope, string session)
SwapWindow(string scope, string session, string srcIndex, string dstIndex)

## 交互与命令
SendKeys(string scope, string session, string targetPane, string keys, bool enter)
  // enter=true 时追加 Enter；这是 GUI 内"伪终端"交互的核心（配合轮询 CapturePane）
CapturePane(string scope, string session, string window, string pane, int lines)
  // window/pane 可为空串表示默认当前；重载替换上文的 CapturePane 三参版本（保留三参为兼容，内部转调）
RunTmuxCommand(string scope, string args)
  // 透传任意 tmux 命令行参数（前端"命令提示符"功能），返回 stdout/stderr 合并文本
ShowOptions(string scope, string session)      // tmux show-options -t <session>（含 -g 全局）
SetOption(string scope, string session, string option, string value)
ListKeys(string scope)                          // tmux list-keys
ListServers()   -> data.servers: [{id, name, host, port, username, authType("password"|"key"), keyPath, lastError?}]
SaveServer(string json)             // json 为上面单个对象（无 id 时生成新 id，有 id 则更新）
DeleteServer(string id)
TestServer(string id)               // 连通性测试，失败时 data.error
ListFavorites() -> data.favorites: [{id, name, command, scope}]
SaveFavorite(string json)
DeleteFavorite(string id)
```

scope 约定：`local`=本机 tmux（PATH 中的 `tmux`，不可用时报告如何安装/或走 WSL）；
`psmux`=本机 psmux（`pwsh`/`powershell` -NoProfile -Command "psmux ..."）；
`ssh:<id>`=通过 SSH.NET 在远程执行 tmux 命令（远程须有 tmux）。

## tmux 命令约定（后端统一实现）
- 列表：`tmux list-sessions -F '#{session_name}\t#{session_created}\t#{session_windows}\t#{session_attached}'`
- 捕获：`tmux capture-pane -t <session> -p -S -<lines>`
- 新建：`tmux new-session -d -s <name> [<command>]`
- 杀：`tmux kill-session -t <name>`；改名：`tmux rename-session -t <old> <new>`
- attach：本地 `wt -w new "bash -lc 'tmux attach -t <name>'"`（无 wt 则 fallback `cmd /c start`）；
  ssh 场景 `wt -w new "ssh -t <user>@<host> -p <port> tmux attach -t <name>"`（密码认证时改用
  已存的 SSH.NET 凭据提示不可用，直接用系统 ssh 依赖密钥；在 UI 上说明）

## 配置持久化
`%APPDATA%\tmux-gui\config.json`：
```json
{"servers":[...],"favorites":[...]}
```
由后端 Services/ConfigStore.cs 独占读写（带文件锁）。

## 前端 UI 功能清单（全部必做）
1. 侧边栏：SSH 服务器列表 + 收藏命令（可新建/编辑/删除、一键用收藏创建会话）
2. 会话面板：会话列表（新建/改名/杀/attach/查看输出）
3. 树形导航：会话 → 窗口 → 面板，三级树，可新建窗口/分屏/杀/切换
4. 输出查看器：选中面板后轮询 CapturePane（1~2s）渲染 `<pre>`，底部输入框用 SendKeys
   实现伪终端交互（含 Enter 发送、Ctrl+C/Special keys 下拉）
5. 工具栏：布局切换（even-h/even-v/main/tiled）、rotate、resize、next/prev window
6. 选项查看/编辑（ShowOptions/SetOption）、tmux 命令提示符（RunTmuxCommand）
7. scope 切换器（local tmux / psmux / 各 SSH 服务器），顶部下拉

## Debug RPC（--debug 开关）
- 启动参数 `--debug [port]`（默认端口 8765）时，宿主额外启动 HTTP JSON-RPC 服务，
  仅绑定 `127.0.0.1`（窗口标题追加 " [debug:port]" 便于识别）。
- 端点 `POST /rpc`：请求体 `{"method":"<Bridge方法名>","params":["参数1", ...]}`，
  通过反射按方法名调用 Bridge 对应方法（params 元素按声明参数类型转换：string/int/bool），
  返回 Bridge 的 JSON 字符串原样作为响应体（application/json）。
  方法不存在返回 `{"ok":false,"error":"unknown method: X"}`，参数不匹配/转换失败返回
  `{"ok":false,"error":"..."}`；HTTP 状态一律 200（错误在 body 内表达）。
- 端点 `GET /methods`：返回全部可调用方法名数组（JSON）。
- 用途：外部 agent 无需 GUI 交互即可调试 tmux 操作（list/capture/send-keys 等）。
- 日志：debug 模式下把每次 RPC 调用（方法名+耗时）追加到 `%LOCALAPPDATA%\tmux-gui\debug.log`。

## 质量红线
- 后端：`dotnet build` 零警告零错误；所有 tmux 输出解析对空输出/会话不存在容错
- 前端：Chrome 108+ 语法（WebView2 Evergreen），中文 UI 文案，深色主题
- 双方不得修改契约；发现契约问题 → 报告主 agent，不得自行改桥签名
