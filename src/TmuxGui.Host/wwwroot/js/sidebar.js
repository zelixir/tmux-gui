// sidebar.js — 侧边栏：SSH 服务器管理 + 收藏命令
"use strict";

const Sidebar = {
  servers: [],
  favorites: [],

  async init() {
    document.getElementById("serversBody").addEventListener("click", (e) => Sidebar.onServersClick(e));
    document.getElementById("favBody").addEventListener("click", (e) => Sidebar.onFavClick(e));
    // 折叠
    document.querySelectorAll(".side-header[data-toggle]").forEach((h) => {
      h.addEventListener("click", () => h.parentElement.classList.toggle("collapsed"));
    });
    await Sidebar.reload();
  },

  async reload() {
    const [sr, fr] = await Promise.all([Bridge.listServers(), Bridge.listFavorites()]);
    Sidebar.servers = checkResult(sr, "加载服务器列表") ? (sr.servers || []) : [];
    Sidebar.favorites = checkResult(fr, "加载收藏列表") ? (fr.favorites || []) : [];
    Sidebar.renderServers();
    Sidebar.renderFavorites();
    Topbar.refreshServers(); // 顶部 scope 切换器同步
  },

  // ---------- SSH 服务器 ----------
  renderServers() {
    const body = document.getElementById("serversBody");
    body.textContent = "";
    if (Sidebar.servers.length === 0) {
      const d = document.createElement("div");
      d.className = "empty";
      d.textContent = "尚未添加 SSH 服务器";
      body.appendChild(d);
      return;
    }
    for (const s of Sidebar.servers) {
      const row = document.createElement("div");
      row.className = "item-row";
      const label = document.createElement("div");
      label.className = "label";
      label.title = `${s.username || "?"}@${s.host}:${s.port || 22}（密钥/agent 认证）`;
      label.textContent = s.name || s.host;
      const sub = document.createElement("span");
      sub.className = "sub";
      sub.textContent = s.host || "";
      label.appendChild(document.createElement("br"));
      label.appendChild(sub);
      const actions = document.createElement("div");
      actions.className = "actions";
      actions.appendChild(btn("测试", "test", s.id));
      actions.appendChild(btn("编辑", "edit", s.id));
      actions.appendChild(btn("删除", "del", s.id, "danger"));
      row.appendChild(label);
      row.appendChild(actions);
      body.appendChild(row);
    }
    function btn(text, act, id, cls) {
      const b = document.createElement("button");
      b.textContent = text;
      if (cls) b.className = cls;
      b.dataset.act = act;
      b.dataset.id = id;
      return b;
    }
  },

  onServersClick(e) {
    const b = e.target.closest("button[data-act]");
    if (!b) return;
    const s = Sidebar.servers.find((x) => x.id === b.dataset.id);
    if (!s) return;
    if (b.dataset.act === "test") Sidebar.testServer(s);
    else if (b.dataset.act === "edit") Modals.serverForm(s);
    else if (b.dataset.act === "del") Sidebar.deleteServer(s);
  },

  async testServer(s) {
    toast(`正在测试 ${s.name || s.host} …`, "info");
    const r = await Bridge.testServer(s.id);
    // 契约：调用成功但连通失败时 r.ok=true 且 r.error 携带原因
    if (r.ok === true && r.error) toast(`${s.name || s.host} 连接失败: ${r.error}`);
    else if (r.ok === true) toast(`${s.name || s.host} 连接成功`, "info");
    else toast(`测试失败: ${r.error || "未知错误"}`);
  },

  async deleteServer(s) {
    if (!confirm(`确定删除服务器「${s.name || s.host}」？`)) return;
    const r = await Bridge.deleteServer(s.id);
    if (checkResult(r, "删除服务器")) { toast("已删除", "info"); await Sidebar.reload(); }
  },

  // ---------- 收藏命令 ----------
  renderFavorites() {
    const body = document.getElementById("favBody");
    body.textContent = "";
    if (Sidebar.favorites.length === 0) {
      const d = document.createElement("div");
      d.className = "empty";
      d.textContent = "尚未添加收藏命令";
      body.appendChild(d);
      return;
    }
    for (const f of Sidebar.favorites) {
      const row = document.createElement("div");
      row.className = "item-row";
      const label = document.createElement("div");
      label.className = "label";
      label.title = f.command || "";
      label.textContent = f.name || f.command;
      const actions = document.createElement("div");
      actions.className = "actions";
      const run = document.createElement("button");
      run.textContent = "新会话";
      run.title = "在当前 scope 用此命令新建会话";
      run.addEventListener("click", () => Tree.newSessionFromFavorite(f));
      actions.appendChild(run);
      const edit = document.createElement("button");
      edit.textContent = "编辑";
      edit.addEventListener("click", () => Modals.favoriteForm(f));
      actions.appendChild(edit);
      const del = document.createElement("button");
      del.textContent = "删";
      del.className = "danger";
      del.addEventListener("click", async () => {
        if (!confirm(`确定删除收藏「${f.name}」？`)) return;
        const r = await Bridge.deleteFavorite(f.id);
        if (checkResult(r, "删除收藏")) { toast("已删除", "info"); await Sidebar.reload(); }
      });
      actions.appendChild(del);
      row.appendChild(label);
      row.appendChild(actions);
      body.appendChild(row);
    }
  },

  onFavClick(e) {
    const b = e.target.closest("button[data-act]");
    if (!b) return;
    const f = Sidebar.favorites.find((x) => x.id === b.dataset.id);
    if (!f) return;
    if (b.dataset.act === "run") Tree.newSessionFromFavorite(f);
    else if (b.dataset.act === "edit") Modals.favoriteForm(f);
    else if (b.dataset.act === "del") Sidebar.deleteFavorite(f);
  },

  async deleteFavorite(f) {
    if (!confirm(`确定删除收藏「${f.name}」？`)) return;
    const r = await Bridge.deleteFavorite(f.id);
    if (checkResult(r, "删除收藏")) { toast("已删除", "info"); await Sidebar.reload(); }
  },
};
