// tree.js — 三级树（会话 → 窗口 → 面板）及其操作
"use strict";

const Tree = {
  sessions: [], // [{name, created, windows, attached, windowsData:[{index,name,active,panesData:[...]}]}]
  expanded: new Set(), // 展开的会话名 / 会话名::窗口index

  async refresh() {
    const r = await Bridge.listSessions(App.scope);
    if (!checkResult(r, "获取会话列表")) { Tree.sessions = []; Tree.render(); return; }
    const sessions = r.sessions || [];
    // 保留展开状态里已消失的会话清理
    const names = new Set(sessions.map((s) => s.name));
    Tree.expanded = new Set([...Tree.expanded].filter((k) => !k.includes("::") ? names.has(k) : names.has(k.split("::")[0])));
    Tree.sessions = sessions.map((s) => ({ ...s, windowsData: null }));
    // 若选中会话还在，保持选中；否则清空
    if (App.selected && !names.has(App.selected.session)) {
      App.selected = null;
      Viewer.stop();
    }
    await Tree.loadExpanded();
    Tree.render();
  },

  async loadExpanded() {
    for (const s of Tree.sessions) {
      if (!Tree.expanded.has(s.name)) continue;
      const wr = await Bridge.listWindows(App.scope, s.name);
      if (!checkResult(wr, "获取窗口列表")) { s.windowsData = []; continue; }
      s.windowsData = (wr.windows || []).map((w) => ({ ...w, panesData: null }));
      for (const w of s.windowsData) {
        if (Tree.expanded.has(`${s.name}::${w.index}`)) {
          const pr = await Bridge.listPanes(App.scope, s.name, String(w.index));
          w.panesData = checkResult(pr, "获取面板列表") ? (pr.panes || []) : [];
        }
      }
    }
  },

  toggleSession(name) {
    if (Tree.expanded.has(name)) Tree.expanded.delete(name);
    else Tree.expanded.add(name);
    Tree.refresh();
  },

  toggleWindow(sess, idx) {
    const key = `${sess}::${idx}`;
    if (Tree.expanded.has(key)) Tree.expanded.delete(key);
    else Tree.expanded.add(key);
    Tree.refresh();
  },

  findSession(name) { return Tree.sessions.find((s) => s.name === name); },
  findWindow(sessName, idx) {
    const s = Tree.findSession(sessName);
    if (!s || !s.windowsData) return null;
    return s.windowsData.find((w) => String(w.index) === String(idx)) || null;
  },

  select(session, win, pane) {
    App.selected = { session, window: win, pane };
    Tree.render();
    Viewer.onSelectionChanged();
  },

  // ---------- 渲染 ----------
  render() {
    const tree = document.getElementById("tree");
    tree.textContent = "";
    if (Tree.sessions.length === 0) {
      const d = document.createElement("div");
      d.className = "tree-empty";
      d.textContent = "没有会话 — 点击上方「新建会话」开始";
      tree.appendChild(d);
      return;
    }
    for (const s of Tree.sessions) {
      tree.appendChild(Tree.sessionNode(s));
      if (Tree.expanded.has(s.name) && s.windowsData) {
        for (const w of s.windowsData) {
          tree.appendChild(Tree.windowNode(s, w));
          if (Tree.expanded.has(`${s.name}::${w.index}`) && w.panesData) {
            for (const p of w.panesData) tree.appendChild(Tree.paneNode(s, w, p));
          }
        }
      }
    }
  },

  sessionNode(s) {
    const node = document.createElement("div");
    node.className = "tree-node level-session";
    const row = document.createElement("div");
    row.className = "node-row";
    if (App.selected && App.selected.session === s.name && App.selected.window == null) row.classList.add("selected");
    const caret = document.createElement("span");
    caret.className = "caret";
    caret.textContent = Tree.expanded.has(s.name) ? "▾" : "▸";
    const label = document.createElement("span");
    label.textContent = s.name;
    const badge = document.createElement("span");
    badge.className = "badge" + (s.attached ? " active" : "");
    badge.textContent = s.attached ? "attached" : `${s.windows} 窗口`;
    const acts = Tree.actions([
      ["attach", "连接", () => Tree.attach(s.name)],
      ["newwin", "+窗口", () => Modals.newWindowFor(s.name)],
      ["ren", "改名", () => Modals.renameSession(s.name)],
      ["kill", "杀", () => Tree.killSession(s.name), "danger"],
    ]);
    row.appendChild(caret);
    row.appendChild(label);
    row.appendChild(badge);
    row.appendChild(acts);
    row.addEventListener("click", (e) => {
      if (e.target.closest("button")) return;
      Tree.toggleSession(s.name);
    });
    node.appendChild(row);
    return node;
  },

  windowNode(s, w) {
    const node = document.createElement("div");
    node.className = "tree-node level-window";
    const row = document.createElement("div");
    row.className = "node-row";
    if (App.selected && App.selected.session === s.name && String(App.selected.window) === String(w.index) && App.selected.pane == null) row.classList.add("selected");
    const caret = document.createElement("span");
    caret.className = "caret";
    caret.textContent = Tree.expanded.has(`${s.name}::${w.index}`) ? "▾" : "▸";
    const label = document.createElement("span");
    label.textContent = `${w.index}: ${w.name}`;
    const badge = document.createElement("span");
    badge.className = "badge" + (w.active ? " active" : "");
    badge.textContent = w.active ? "active" : `${w.panes} 面板`;
    const acts = Tree.actions([
      ["sel", "切换", () => Tree.selectWindow(s.name, w.index)],
      ["ren", "改名", () => Modals.renameWindow(s.name, w.index)],
      ["kill", "杀", () => Tree.killWindow(s.name, w.index), "danger"],
    ]);
    row.appendChild(caret);
    row.appendChild(label);
    row.appendChild(badge);
    row.appendChild(acts);
    row.addEventListener("click", (e) => {
      if (e.target.closest("button")) return;
      Tree.toggleWindow(s.name, w.index);
    });
    node.appendChild(row);
    return node;
  },

  paneNode(s, w, p) {
    const node = document.createElement("div");
    node.className = "tree-node level-pane";
    const row = document.createElement("div");
    row.className = "node-row";
    const sel = App.selected;
    if (sel && sel.session === s.name && String(sel.window) === String(w.index) && String(sel.pane) === String(p.index)) row.classList.add("selected");
    const caret = document.createElement("span");
    caret.className = "caret";
    caret.textContent = p.active ? "●" : "○";
    const label = document.createElement("span");
    const title = (p.title || "").trim();
    label.textContent = `面板 ${p.index}${title ? " — " + title : ""} (${p.width}x${p.height})`;
    label.title = `${p.cwd || ""} pid:${p.pid || "?"}`;
    const badge = document.createElement("span");
    if (p.active) { badge.className = "badge active"; badge.textContent = "active"; }
    if (p.dead) { badge.className = "badge dead"; badge.textContent = "dead"; }
    const acts = Tree.actions([
      ["sel", "查看", () => Tree.select(s.name, w.index, p.index)],
      ["splitH", "|分", () => Tree.split(s.name, p.index, "h")],
      ["splitV", "—分", () => Tree.split(s.name, p.index, "v")],
      ["kill", "杀", () => Tree.killPane(s.name, p.index), "danger"],
    ]);
    row.appendChild(caret);
    row.appendChild(label);
    if (badge.textContent) row.appendChild(badge);
    row.appendChild(acts);
    row.addEventListener("click", (e) => {
      if (e.target.closest("button")) return;
      Tree.select(s.name, w.index, p.index);
    });
    node.appendChild(row);
    return node;
  },

  actions(defs) {
    const wrap = document.createElement("div");
    wrap.className = "node-actions";
    for (const [act, text, fn, cls] of defs) {
      const b = document.createElement("button");
      b.textContent = text;
      if (cls) b.className = cls;
      b.addEventListener("click", (e) => { e.stopPropagation(); fn(); });
      wrap.appendChild(b);
    }
    return wrap;
  },

  // ---------- 操作 ----------
  async attach(name) {
    const r = await Bridge.attach(App.scope, name);
    if (checkResult(r, "attach")) toast(`已在 Windows Terminal 中 attach「${name}」`, "info");
  },

  async selectWindow(sess, idx) {
    const r = await Bridge.selectWindow(App.scope, sess, String(idx));
    if (checkResult(r, "切换窗口")) await Tree.refresh();
  },

  async killSession(name) {
    if (!(await Modals.confirmKill(`确定杀掉会话「${name}」？其中所有窗口与进程都会终止。`, () => Bridge.killSession(App.scope, name)))) return;
    if (App.selected && App.selected.session === name) { App.selected = null; Viewer.stop(); }
    toast(`会话「${name}」已终止`, "info");
    await Tree.refresh();
  },

  async killWindow(sess, idx) {
    if (!(await Modals.confirmKill(`确定杀掉会话「${sess}」的窗口 ${idx}？`, () => Bridge.killWindow(App.scope, sess, String(idx))))) return;
    const sel = App.selected;
    if (sel && sel.session === sess && String(sel.window) === String(idx)) { App.selected = null; Viewer.stop(); }
    await Tree.refresh();
  },

  async killPane(sess, pane) {
    if (!(await Modals.confirmKill(`确定杀掉面板 ${pane}？`, () => Bridge.killPane(App.scope, sess, String(pane))))) return;
    const sel = App.selected;
    if (sel && sel.session === sess && String(sel.pane) === String(pane)) { App.selected = null; Viewer.stop(); }
    await Tree.refresh();
  },

  async split(sess, pane, dir) {
    const r = await Bridge.splitPane(App.scope, sess, pane != null ? String(pane) : "", dir, "");
    if (checkResult(r, "分屏")) await Tree.refresh();
  },

  async newSessionFromFavorite(f) {
    const r = await Bridge.newSession(App.scope, f.name || "", f.command || "");
    if (checkResult(r, "新建会话")) {
      toast(`已用收藏「${f.name}」新建会话`, "info");
      await Tree.refresh();
    }
  },
};
