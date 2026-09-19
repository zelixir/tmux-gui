// bridge.js — C# 桥接封装（所有方法均为异步 Task<string>，返回 JSON）
"use strict";

const Bridge = {
  available: !!(window.chrome && window.chrome.webview && window.chrome.webview.hostObjects && window.chrome.webview.hostObjects.bridge),

  /** 调用桥接方法并解析 JSON；异常统一转为 {ok:false,error} */
  async call(method, ...args) {
    if (!Bridge.available) {
      return { ok: false, error: "桥接不可用：未在 WebView2 宿主中运行（bridge hostObject 未注册）" };
    }
    try {
      const raw = await window.chrome.webview.hostObjects.bridge[method](...args);
      if (typeof raw !== "string" || raw.length === 0) {
        return { ok: false, error: `桥接方法 ${method} 返回了空结果` };
      }
      return JSON.parse(raw);
    } catch (e) {
      return { ok: false, error: `${method} 调用失败: ${errText(e)}` };
    }
  },

  // ---- 便捷封装 ----
  listSessions(scope) { return Bridge.call("ListSessions", scope); },
  listWindows(scope, session) { return Bridge.call("ListWindows", scope, session); },
  listPanes(scope, session, win) { return Bridge.call("ListPanes", scope, session, win); },
  capturePane(scope, session, win, pane, lines) { return Bridge.call("CapturePane", scope, session, win, pane, lines); },
  newSession(scope, name, command) { return Bridge.call("NewSession", scope, name, command || ""); },
  newWindow(scope, session, name, command) { return Bridge.call("NewWindow", scope, session, name, command || ""); },
  renameSession(scope, oldName, newName) { return Bridge.call("RenameSession", scope, oldName, newName); },
  renameWindow(scope, session, winIdx, newName) { return Bridge.call("RenameWindow", scope, session, winIdx, newName); },
  killSession(scope, name) { return Bridge.call("KillSession", scope, name); },
  killWindow(scope, session, winIdx) { return Bridge.call("KillWindow", scope, session, winIdx); },
  killPane(scope, session, pane) { return Bridge.call("KillPane", scope, session, pane); },
  selectWindow(scope, session, winIdx) { return Bridge.call("SelectWindow", scope, session, winIdx); },
  selectPane(scope, session, pane) { return Bridge.call("SelectPane", scope, session, pane); },
  attach(scope, name) { return Bridge.call("Attach", scope, name); },
  splitPane(scope, session, pane, dir, command) { return Bridge.call("SplitPane", scope, session, pane, dir, command || ""); },
  resizePane(scope, session, pane, dir, cells) { return Bridge.call("ResizePane", scope, session, pane, dir, cells); },
  rotatePane(scope, session, winIdx, dir) { return Bridge.call("RotatePane", scope, session, winIdx, dir); },
  selectLayout(scope, session, winIdx, layout) { return Bridge.call("SelectLayout", scope, session, winIdx, layout); },
  nextWindow(scope, session) { return Bridge.call("NextWindow", scope, session); },
  prevWindow(scope, session) { return Bridge.call("PreviousWindow", scope, session); },
  sendKeys(scope, session, pane, keys, enter) { return Bridge.call("SendKeys", scope, session, pane, keys, enter); },
  runTmuxCommand(scope, args) { return Bridge.call("RunTmuxCommand", scope, args); },
  showOptions(scope, session) { return Bridge.call("ShowOptions", scope, session); },
  setOption(scope, session, option, value) { return Bridge.call("SetOption", scope, session, option, value); },

  listServers() { return Bridge.call("ListServers"); },
  saveServer(obj) { return Bridge.call("SaveServer", JSON.stringify(obj)); },
  deleteServer(id) { return Bridge.call("DeleteServer", id); },
  testServer(id) { return Bridge.call("TestServer", id); },

  listFavorites() { return Bridge.call("ListFavorites"); },
  saveFavorite(obj) { return Bridge.call("SaveFavorite", JSON.stringify(obj)); },
  deleteFavorite(id) { return Bridge.call("DeleteFavorite", id); },
};

function errText(e) {
  if (e instanceof Error) return e.message;
  try { return String(e); } catch (_) { return "未知错误"; }
}

/** HTML 转义（会话/窗口名等来自 shell 输出，必须转义防 XSS） */
function esc(s) {
  return String(s == null ? "" : s)
    .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
}

/** 统一 toast：r.ok===false 时报错；也支持手动提示 */
function toast(msg, kind) {
  const box = document.getElementById("toasts");
  const el = document.createElement("div");
  el.className = "toast" + (kind === "info" ? " info" : "");
  el.textContent = msg;
  box.appendChild(el);
  setTimeout(() => el.remove(), kind === "info" ? 2500 : 6000);
}

/** 对桥接结果做统一错误检查；返回是否成功 */
function checkResult(r, action) {
  if (!r || r.ok !== true) {
    toast(`${action}失败: ${(r && r.error) || "未知错误"}`);
    return false;
  }
  return true;
}
