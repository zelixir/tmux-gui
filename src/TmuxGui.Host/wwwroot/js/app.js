// app.js — 应用入口：scope 切换、顶部栏、初始化
"use strict";

const App = {
  scope: "local",

  async setScope(scope) {
    App.scope = scope;
    // scope 切换：清空树与选中，重载
    App.selected = null;
    Viewer.stop();
    Tree.sessions = [];
    Tree.expanded = new Set();
    Tree.render();
    Viewer.onSelectionChanged();
    localStorage.setItem("tmuxgui.scope", scope);
    await Tree.refresh();
  },

  async init() {
    App.selected = null;
    if (!Bridge.available) {
      toast("桥接不可用：请在 tmux-gui (WebView2) 宿主中打开本页面");
    }

    Topbar.init();
    Viewer.init();
    await Sidebar.init();

    // 树工具栏按钮
    document.getElementById("btnNewSession").addEventListener("click", () => Modals.newSession());
    document.getElementById("btnNewWindow").addEventListener("click", () => Modals.newWindow());
    document.getElementById("btnSplitH").addEventListener("click", () => Modals.split("h"));
    document.getElementById("btnSplitV").addEventListener("click", () => Modals.split("v"));
    document.getElementById("btnNextWin").addEventListener("click", async () => {
      const r = await Bridge.nextWindow(App.scope, App.selected ? App.selected.session : "");
      if (checkResult(r, "下一窗口")) await Tree.refresh();
    });
    document.getElementById("btnPrevWin").addEventListener("click", async () => {
      const r = await Bridge.prevWindow(App.scope, App.selected ? App.selected.session : "");
      if (checkResult(r, "上一窗口")) await Tree.refresh();
    });
    document.getElementById("btnRefresh").addEventListener("click", () => Tree.refresh());

    // 抽屉
    document.getElementById("btnCmdDrawer").addEventListener("click", () => CmdDrawer.open());
    document.getElementById("btnOptDrawer").addEventListener("click", () => OptionsDrawer.open());
    document.getElementById("drawerClose").addEventListener("click", () => CmdDrawer.close());
    document.getElementById("drawerMask").addEventListener("click", () => CmdDrawer.close());
    document.getElementById("cmdRunBtn").addEventListener("click", () => CmdDrawer.run());
    document.getElementById("cmdInputBox").addEventListener("keydown", (e) => {
      if (e.key === "Enter") { e.preventDefault(); CmdDrawer.run(); }
    });
    document.getElementById("optionsReload").addEventListener("click", () => OptionsDrawer.load());

    // 模态
    document.getElementById("modalMask").addEventListener("mousedown", (e) => {
      if (e.target === e.currentTarget) Modals.close();
    });

    // 恢复上次 scope
    const saved = localStorage.getItem("tmuxgui.scope") || "local";
    const sel = document.getElementById("scopeSelect");
    sel.value = [...sel.options].some((o) => o.value === saved) ? saved : "local";
    App.scope = sel.value;
    await Tree.refresh();

    // 定时刷新树（较温和，5s），保证会话/窗口/面板状态同步
    setInterval(() => {
      if (!document.hidden) Tree.refresh();
    }, 5000);
  },
};

const Topbar = {
  init() {
    const sel = document.getElementById("scopeSelect");
    sel.addEventListener("change", () => App.setScope(sel.value));
    Topbar.refreshServers();
  },

  /** 用服务器列表填充 scope 下拉（保留本地两项） */
  refreshServers() {
    const sel = document.getElementById("scopeSelect");
    const cur = sel.value;
    sel.textContent = "";
    sel.appendChild(new Option("本机 tmux", "local"));
    sel.appendChild(new Option("本机 psmux", "psmux"));
    const group = document.createElement("optgroup");
    group.label = "SSH 服务器";
    for (const s of Sidebar.servers) group.appendChild(new Option(s.name || s.host, `ssh:${s.id}`));
    sel.appendChild(group);
    // 恢复选择
    if ([...sel.options].some((o) => o.value === cur)) sel.value = cur;
  },
};

window.addEventListener("DOMContentLoaded", () => App.init());
