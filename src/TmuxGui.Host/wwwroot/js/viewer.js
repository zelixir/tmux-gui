// viewer.js — 输出查看器（轮询 CapturePane）+ 底部输入条 + 查看器工具栏
"use strict";

const POLL_MS = 1500;
const CAPTURE_LINES = 2000;

const Viewer = {
  timer: null,
  polling: false,
  follow: true, // 用户上滚时暂停跟随
  lastKey: "",

  onSelectionChanged() {
    Viewer.stop();
    const sel = App.selected;
    const title = document.getElementById("viewerTitle");
    const empty = document.getElementById("viewerEmpty");
    const wrap = document.getElementById("outputWrap");
    if (!sel || sel.pane == null) {
      title.textContent = "（未选择面板）";
      document.getElementById("output").textContent = "";
      empty.style.display = "flex";
      wrap.style.display = "none";
      return;
    }
    empty.style.display = "none";
    wrap.style.display = "block";
    title.textContent = `${sel.session} : 窗口 ${sel.window} : 面板 ${sel.pane}`;
    Viewer.follow = true;
    Viewer.capture(true);
    Viewer.start();
  },

  start() {
    Viewer.stop();
    Viewer.timer = setInterval(() => Viewer.capture(false), POLL_MS);
  },

  stop() {
    if (Viewer.timer) { clearInterval(Viewer.timer); Viewer.timer = null; }
  },

  async capture(first) {
    const sel = App.selected;
    if (!sel || sel.pane == null) return;
    // 防止并发轮询堆叠
    if (Viewer.polling) return;
    Viewer.polling = true;
    try {
      // 五参版 CapturePane：window/pane 传面板索引；传空串则取当前面板
      const r = await Bridge.capturePane(
        App.scope, sel.session,
        sel.window != null ? String(sel.window) : "",
        sel.pane != null ? String(sel.pane) : "",
        CAPTURE_LINES
      );
      // 选中已切换则丢弃旧结果
      if (App.selected !== sel) return;
      if (r.ok === true && r.error) {
        // 调用成功但捕获失败（如 TestServer 型半错误）
        Viewer.setStatus(`捕获失败: ${r.error}`);
        return;
      }
      if (!checkResult(r, "捕获面板输出")) return;
      const output = r.output != null ? r.output : "";
      const pre = document.getElementById("output");
      pre.textContent = output;
      if (Viewer.follow) pre.parentElement.scrollTop = pre.parentElement.scrollHeight;
      Viewer.setStatus("");
    } finally {
      Viewer.polling = false;
    }
  },

  setStatus(text) {
    document.getElementById("viewerTitle").textContent = Viewer.titleBase + (text ? `  —  ${text}` : "");
  },

  get titleBase() {
    const sel = App.selected;
    if (!sel || sel.pane == null) return "（未选择面板）";
    return `${sel.session} : 窗口 ${sel.window} : 面板 ${sel.pane}`;
  },

  init() {
    const wrap = document.getElementById("outputWrap");
    wrap.addEventListener("scroll", () => {
      // 距底部 < 30px 视为在底部，恢复跟随
      Viewer.follow = wrap.scrollHeight - wrap.scrollTop - wrap.clientHeight < 30;
    });

    // 布局切换
    const layoutSel = document.getElementById("layoutSelect");
    layoutSel.addEventListener("change", async () => {
      const sel = App.selected;
      if (!sel || sel.window == null) { toast("请先选择窗口"); layoutSel.value = ""; return; }
      const r = await Bridge.selectLayout(App.scope, sel.session, String(sel.window), layoutSel.value);
      if (checkResult(r, "切换布局")) await Tree.refresh();
    });

    // rotate / resize
    document.getElementById("rotF").addEventListener("click", () => Viewer.rotate("f"));
    document.getElementById("rotB").addEventListener("click", () => Viewer.rotate("b"));
    for (const dir of ["L", "R", "U", "D"]) {
      document.getElementById(`rs${dir}`).addEventListener("click", () => Viewer.resize(dir));
    }

    // 底部输入条
    const input = document.getElementById("cmdInput");
    input.addEventListener("keydown", (e) => {
      if (e.key === "Enter") {
        e.preventDefault();
        Viewer.send();
      }
    });
    document.getElementById("sendBtn").addEventListener("click", () => Viewer.send());

    // 特殊键（SendKeys enter=false，tmux 键名）
    const specials = { "C-c": "C-c", "C-d": "C-d", Tab: "Tab", "↑": "Up", "↓": "Down", Esc: "Escape" };
    for (const [label, keys] of Object.entries(specials)) {
      const b = document.getElementById(`sp-${label.replace("C-", "C")}`);
      if (b) b.addEventListener("click", () => Viewer.sendSpecial(keys));
    }
  },

  async rotate(dir) {
    const sel = App.selected;
    if (!sel || sel.window == null) { toast("请先选择窗口"); return; }
    const r = await Bridge.rotatePane(App.scope, sel.session, String(sel.window), dir);
    if (checkResult(r, "轮换面板")) await Tree.refresh();
  },

  async resize(dir) {
    const sel = App.selected;
    if (!sel) { toast("请先选择面板"); return; }
    const target = sel.pane != null ? String(sel.pane) : "";
    const r = await Bridge.resizePane(App.scope, sel.session, target, dir, 1);
    if (checkResult(r, "调整面板大小")) await Tree.refresh();
  },

  async send() {
    const sel = App.selected;
    if (!sel || sel.pane == null) { toast("请先在左侧树中选择一个面板"); return; }
    const input = document.getElementById("cmdInput");
    const text = input.value;
    input.value = "";
    // enter=true：发送文本并回车；空文本仅回车
    const r = await Bridge.sendKeys(App.scope, sel.session, String(sel.pane), text, true);
    if (!checkResult(r, "发送输入")) return;
    setTimeout(() => Viewer.capture(false), 250);
  },

  async sendSpecial(keys) {
    const sel = App.selected;
    if (!sel || sel.pane == null) { toast("请先在左侧树中选择一个面板"); return; }
    const r = await Bridge.sendKeys(App.scope, sel.session, String(sel.pane), keys, false);
    if (!checkResult(r, `发送 ${keys}`)) return;
    setTimeout(() => Viewer.capture(false), 250);
  },
};
