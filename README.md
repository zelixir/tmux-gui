# tmux-gui

Windows 桌面端 tmux / psmux 会话管理 GUI。基于 **.NET 8 (WinForms) + WebView2** 宿主，前端为纯 HTML/CSS/JS 单页，C# 后端通过 WebView2 host object 桥接暴露全部 tmux 操作。

## 功能

- **会话管理**：列出/新建/重命名/杀掉会话，一键 attach（调起 Windows Terminal）
- **完整对象模型**：会话 → 窗口 → 面板三级树；新建窗口、水平/垂直分屏、杀/切换面板、resize、rotate、换窗口位置、next/prev window
- **布局**：even-horizontal / even-vertical / main-pane / tiled 一键切换
- **输出查看 + 伪终端**：轮询 `capture-pane` 实时查看面板输出；输入条通过 `send-keys` 向面板发送命令（支持 Enter、C-c、C-d、Tab、方向键等特殊键）
- **收藏命令**：常用命令收藏，一键用收藏创建会话
- **SSH 远程管理**：管理任意 SSH 服务器上的 tmux 会话（走系统 OpenSSH，密钥/agent 认证），attach 直接拉起 `wt ssh -t ... tmux attach`
- **psmux 支持**：本机未装 tmux 时可用 PowerShell 实现的 psmux
- **tmux 命令提示符**：透传任意 tmux 命令；查看/编辑会话与全局选项

## 构建 & 运行

需要 Windows 10+、[.NET 8 SDK](https://dotnet.microsoft.com/)、WebView2 Runtime（Win11 自带）。

```bash
dotnet build -c Release
dotnet run --project src/TmuxGui.Host -c Release
```

远程 SSH 管理需本机 `ssh.exe`（Windows 自带）能免密登录目标服务器（推荐 `~/.ssh/id_ed25519` + ssh-agent，或配置 keyPath）。

## 架构

```
src/TmuxGui.Host
├── Program.cs / MainForm.cs     # WinForms 宿主 + WebView2（https://app.local → wwwroot）
├── Bridge/                      # JS 桥接（AddHostObjectToScript("bridge")，33 个方法，JSON 协议）
├── Services/                    # TmuxRunner（local/psmux/ssh 三种 scope）、ConfigStore
└── wwwroot/                     # 前端单页（无构建步骤、无外部依赖）
```

- scope 语义：`local`（本机 tmux）、`psmux`（PowerShell 版）、`ssh:<serverId>`（远程 tmux）
- 配置持久化：`%APPDATA%\tmux-gui\config.json`（服务器与收藏）
- 契约文档：[docs/CONTRACT.md](docs/CONTRACT.md)
