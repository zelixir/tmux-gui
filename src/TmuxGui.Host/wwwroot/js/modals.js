// modals.js — 模态表单（服务器/收藏/新建会话/新建窗口/重命名/分屏）与抽屉（命令提示符/选项）
"use strict";

const Modals = {
  /** 打开自定义模态：title, bodyBuilder(bodyEl), onOk -> Promise<bool> */
  open(title, bodyBuilder, onOk) {
    const mask = document.getElementById("modalMask");
    const body = document.getElementById("modalBody");
    document.getElementById("modalTitle").textContent = title;
    body.textContent = "";
    const footer = document.getElementById("modalFooter");
    footer.textContent = "";
    const cancel = document.createElement("button");
    cancel.textContent = "取消";
    cancel.addEventListener("click", () => Modals.close());
    const ok = document.createElement("button");
    ok.className = "primary";
    ok.textContent = "确定";
    ok.addEventListener("click", async () => {
      ok.disabled = true;
      try {
        const done = await onOk();
        if (done !== false) Modals.close();
      } finally { ok.disabled = false; }
    });
    footer.appendChild(cancel);
    footer.appendChild(ok);
    bodyBuilder(body, ok);
    mask.classList.add("show");
    const first = body.querySelector("input, select, textarea");
    if (first) first.focus();
  },

  close() { document.getElementById("modalMask").classList.remove("show"); },

  formRow(labelText, inputEl, hint) {
    const row = document.createElement("div");
    row.className = "form-row";
    const l = document.createElement("label");
    l.textContent = labelText;
    row.appendChild(l);
    row.appendChild(inputEl);
    if (hint) {
      const h = document.createElement("div");
      h.className = "form-hint";
      h.textContent = hint;
      row.appendChild(h);
    }
    return row;
  },

  input(value, placeholder) {
    const i = document.createElement("input");
    i.value = value || "";
    if (placeholder) i.placeholder = placeholder;
    return i;
  },

  // ---------- SSH 服务器表单 ----------
  serverForm(existing) {
    const s = existing || {};
    const name = Modals.input(s.name, "如：我的开发机");
    const host = Modals.input(s.host, "hostname 或 IP");
    const port = Modals.input(s.port || "22", "22");
    const user = Modals.input(s.username, "SSH 用户名");
    const keyPath = Modals.input(s.keyPath, "可选，如 C:\\Users\\me\\.ssh\\id_ed25519");
    const result = document.createElement("div");
    result.className = "test-result";

    Modals.open(existing ? "编辑 SSH 服务器" : "新增 SSH 服务器", (body, okBtn) => {
      body.appendChild(Modals.formRow("名称", name));
      body.appendChild(Modals.formRow("主机", host));
      body.appendChild(Modals.formRow("端口", port));
      body.appendChild(Modals.formRow("用户名", user));
      body.appendChild(Modals.formRow("密钥路径", keyPath, "认证仅支持密钥 / ssh-agent，不支持密码；密钥路径留空则使用默认密钥或 agent"));
      body.appendChild(result);
      const testBtn = document.createElement("button");
      testBtn.textContent = "测试连接";
      testBtn.addEventListener("click", async () => {
        result.className = "test-result";
        result.textContent = "测试中…";
        // 先保存拿到 id，再测试（未保存的新服务器也能测）
        const saved = await Modals.persistServer({ id: s.id, name: name.value, host: host.value, port: port.value, username: user.value, keyPath: keyPath.value });
        if (!saved) { result.className = "test-result err"; result.textContent = "保存失败，无法测试"; return; }
        s.id = saved.id;
        const r = await Bridge.testServer(saved.id);
        if (r.ok === true && !r.error) { result.className = "test-result ok"; result.textContent = "连接成功"; }
        else { result.className = "test-result err"; result.textContent = `连接失败: ${(r.ok && r.error) || r.error || "未知错误"}`; }
      });
      body.appendChild(testBtn);
    }, async () => {
      if (!host.value.trim()) { toast("主机不能为空"); return false; }
      const saved = await Modals.persistServer({ id: s.id, name: name.value, host: host.value, port: port.value, username: user.value, keyPath: keyPath.value });
      return !!saved;
    });
  },

  async persistServer(obj) {
    const r = await Bridge.saveServer(obj);
    if (!checkResult(r, "保存服务器")) return null;
    await Sidebar.reload();
    return (r.server) || obj;
  },

  // ---------- 收藏命令表单 ----------
  favoriteForm(existing) {
    const f = existing || {};
    const name = Modals.input(f.name, "如：日志监控");
    const cmd = Modals.input(f.command, "如：tail -f /var/log/syslog");
    Modals.open(existing ? "编辑收藏命令" : "新增收藏命令", (body) => {
      body.appendChild(Modals.formRow("名称", name));
      body.appendChild(Modals.formRow("命令", cmd));
    }, async () => {
      if (!name.value.trim() || !cmd.value.trim()) { toast("名称与命令不能为空"); return false; }
      const r = await Bridge.saveFavorite({ id: f.id, name: name.value, command: cmd.value, scope: f.scope || "" });
      if (!checkResult(r, "保存收藏")) return false;
      await Sidebar.reload();
      return true;
    });
  },

  // ---------- 新建会话 ----------
  newSession() {
    const name = Modals.input("", "会话名（留空自动命名）");
    const cmd = Modals.input("", "可选启动命令");
    const fav = document.createElement("select");
    fav.appendChild(new Option("— 从收藏选择命令 —", ""));
    for (const f of Sidebar.favorites) fav.appendChild(new Option(`${f.name}（${f.command}）`, f.id));
    fav.addEventListener("change", () => {
      const f = Sidebar.favorites.find((x) => x.id === fav.value);
      if (f) { name.value = f.name; cmd.value = f.command; }
    });
    Modals.open("新建会话（当前 scope）", (body) => {
      body.appendChild(Modals.formRow("从收藏填充", fav));
      body.appendChild(Modals.formRow("会话名", name));
      body.appendChild(Modals.formRow("启动命令", cmd, "留空使用默认 shell"));
    }, async () => {
      const r = await Bridge.newSession(App.scope, name.value.trim(), cmd.value.trim());
      if (!checkResult(r, "新建会话")) return false;
      await Tree.refresh();
      return true;
    });
  },

  // ---------- 新建窗口 ----------
  newWindow() {
    const sess = App.selected ? App.selected.session : (Tree.sessions[0] && Tree.sessions[0].name) || "";
    if (!sess) { toast("当前没有会话"); return; }
    const name = Modals.input("", "窗口名（留空自动命名）");
    const cmd = Modals.input("", "可选启动命令");
    const fav = document.createElement("select");
    fav.appendChild(new Option("— 从收藏选择命令 —", ""));
    for (const f of Sidebar.favorites) fav.appendChild(new Option(`${f.name}（${f.command}）`, f.id));
    fav.addEventListener("change", () => {
      const f = Sidebar.favorites.find((x) => x.id === fav.value);
      if (f) { name.value = f.name; cmd.value = f.command; }
    });
    Modals.open(`在会话「${sess}」新建窗口`, (body) => {
      body.appendChild(Modals.formRow("从收藏填充", fav));
      body.appendChild(Modals.formRow("窗口名", name));
      body.appendChild(Modals.formRow("启动命令", cmd));
    }, async () => {
      const r = await Bridge.newWindow(App.scope, sess, name.value.trim(), cmd.value.trim());
      if (!checkResult(r, "新建窗口")) return false;
      await Tree.refresh();
      return true;
    });
  },

  // ---------- 重命名 ----------
  rename(kind) {
    const sel = App.selected;
    if (!sel) { toast("请先选择会话或窗口"); return; }
    if (kind === "session") {
      const input = Modals.input(sel.session);
      Modals.open("重命名会话", (body) => body.appendChild(Modals.formRow("新名称", input)), async () => {
        const r = await Bridge.renameSession(App.scope, sel.session, input.value.trim());
        if (!checkResult(r, "重命名会话")) return false;
        App.selected = null;
        await Tree.refresh();
        return true;
      });
    } else {
      if (!sel.window && sel.window !== "0") { toast("请先选择窗口"); return; }
      const win = Tree.findWindow(sel.session, sel.window);
      const input = Modals.input(win ? win.name : "");
      Modals.open("重命名窗口", (body) => body.appendChild(Modals.formRow("新名称", input)), async () => {
        const r = await Bridge.renameWindow(App.scope, sel.session, String(sel.window), input.value.trim());
        if (!checkResult(r, "重命名窗口")) return false;
        await Tree.refresh();
        return true;
      });
    }
  },

  // ---------- 分屏 ----------
  split(dir) {
    const sel = App.selected;
    const sess = sel ? sel.session : (Tree.sessions[0] && Tree.sessions[0].name) || "";
    if (!sess) { toast("当前没有会话"); return; }
    const pane = sel && sel.pane != null ? String(sel.pane) : "";
    const cmd = Modals.input("", "可选新面板启动命令");
    Modals.open(dir === "h" ? "垂直分屏（左右）" : "水平分屏（上下）", (body) => {
      body.appendChild(Modals.formRow("启动命令", cmd, "留空使用默认 shell"));
    }, async () => {
      const r = await Bridge.splitPane(App.scope, sess, pane, dir, cmd.value.trim());
      if (!checkResult(r, "分屏")) return false;
      await Tree.refresh();
      return true;
    });
  },

  // ---------- 确认类危险操作 ----------
  async confirmKill(text, action) {
    if (!confirm(text)) return false;
    const r = await action();
    return checkResult(r, "删除操作");
  },
};

// ============ 抽屉：tmux 命令提示符 ============
const CmdDrawer = {
  open() {
    document.getElementById("drawerMask").classList.add("show");
    const drawer = document.getElementById("drawer");
    drawer.classList.add("show");
    document.getElementById("cmdInputBox").focus();
  },
  close() {
    document.getElementById("drawerMask").classList.remove("show");
    document.getElementById("drawer").classList.remove("show");
  },
  async run() {
    const input = document.getElementById("cmdInputBox");
    const out = document.getElementById("cmdOutput");
    const args = input.value.trim();
    if (!args) return;
    out.textContent += `$ tmux ${args}\n`;
    input.value = "";
    const r = await Bridge.runTmuxCommand(App.scope, args);
    if (r.ok === true) {
      const text = (r.output != null ? r.output : "") || "(无输出)";
      out.textContent += text + "\n";
    } else {
      out.textContent += `错误: ${r.error || "未知"}\n`;
    }
    out.scrollTop = out.scrollHeight;
  },
};

// ============ 抽屉：tmux 选项查看/编辑 ============
const OptionsDrawer = {
  open() {
    document.getElementById("drawerMask").classList.add("show");
    document.getElementById("drawer").classList.add("show");
    document.getElementById("drawerTitle").textContent = "tmux 选项";
    OptionsDrawer.load();
  },

  async load() {
    const body = document.getElementById("optionsList");
    body.textContent = "加载中…";
    const r = await Bridge.showOptions(App.scope, App.selected ? App.selected.session : "");
    if (!checkResult(r, "读取选项")) { body.textContent = ""; return; }
    body.textContent = "";
    const lines = String(r.options != null ? r.options : "").split(/\r?\n/).filter((l) => l.trim());
    if (lines.length === 0) { body.textContent = "（无选项输出）"; return; }
    for (const line of lines) {
      // 形如 "option-name value"（全局选项可能带 @ 前缀）
      const m = line.match(/^(\S+)\s+(.*)$/);
      if (!m) continue;
      const row = document.createElement("div");
      row.className = "opt-row";
      const name = document.createElement("span");
      name.className = "opt-name";
      name.textContent = m[1];
      const val = document.createElement("span");
      val.className = "opt-value";
      val.textContent = m[2];
      const editBtn = document.createElement("button");
      editBtn.textContent = "编辑";
      editBtn.addEventListener("click", () => OptionsDrawer.editOption(m[1], m[2]));
      row.appendChild(name);
      row.appendChild(val);
      row.appendChild(editBtn);
      body.appendChild(row);
    }
  },

  editOption(name, oldVal) {
    const input = Modals.input(oldVal);
    Modals.open(`设置选项 ${name}`, (body) => body.appendChild(Modals.formRow("值", input)), async () => {
      const r = await Bridge.setOption(App.scope, App.selected ? App.selected.session : "", name, input.value);
      if (!checkResult(r, "设置选项")) return false;
      toast("选项已设置", "info");
      await OptionsDrawer.load();
      return true;
    });
  },
};
