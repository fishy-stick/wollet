"use strict";

const state = {
  devices: [],
  selectedId: readSelectedDevice(),
  events: null,
  busyDeviceId: null,
  confirmAction: null,
  tokenTimer: null,
  toastTimer: null,
};

const elements = {};

document.addEventListener("DOMContentLoaded", () => {
  Object.assign(elements, {
    bootView: document.querySelector("#boot-view"),
    loginView: document.querySelector("#login-view"),
    appView: document.querySelector("#app-view"),
    loginForm: document.querySelector("#login-form"),
    username: document.querySelector("#username"),
    password: document.querySelector("#password"),
    togglePassword: document.querySelector("#toggle-password"),
    loginButton: document.querySelector("#login-button"),
    loginError: document.querySelector("#login-error"),
    createToken: document.querySelector("#create-token"),
    emptyCreateToken: document.querySelector("#empty-create-token"),
    logout: document.querySelector("#logout"),
    deviceFocus: document.querySelector("#device-focus"),
    emptyState: document.querySelector("#empty-state"),
    deviceName: document.querySelector("#device-name"),
    deviceStatus: document.querySelector("#device-status"),
    deviceMeta: document.querySelector("#device-meta"),
    deviceAction: document.querySelector("#device-action"),
    deviceActionLabel: document.querySelector("#device-action-label"),
    deviceRail: document.querySelector("#device-rail"),
    removeDevice: document.querySelector("#remove-device"),
    tokenDialog: document.querySelector("#token-dialog"),
    tokenValue: document.querySelector("#token-value"),
    tokenCountdown: document.querySelector("#token-countdown"),
    copyToken: document.querySelector("#copy-token"),
    confirmDialog: document.querySelector("#confirm-dialog"),
    confirmTitle: document.querySelector("#confirm-title"),
    confirmMessage: document.querySelector("#confirm-message"),
    confirmCancel: document.querySelector("#confirm-cancel"),
    confirmSubmit: document.querySelector("#confirm-submit"),
    toast: document.querySelector("#toast"),
  });

  bindInteractions();
  bootstrap();
});

function bindInteractions() {
  elements.loginForm.addEventListener("submit", login);
  elements.togglePassword.addEventListener("click", togglePassword);
  elements.createToken.addEventListener("click", createPairingToken);
  elements.emptyCreateToken.addEventListener("click", createPairingToken);
  elements.logout.addEventListener("click", logout);
  elements.deviceAction.addEventListener("click", confirmDeviceAction);
  elements.removeDevice.addEventListener("click", confirmRemoveDevice);
  elements.copyToken.addEventListener("click", copyToken);
  elements.tokenDialog.addEventListener("close", clearTokenDialog);
  elements.confirmCancel.addEventListener("click", () => elements.confirmDialog.close());
  elements.confirmSubmit.addEventListener("click", runConfirmedAction);
}

async function bootstrap() {
  try {
    await api("/api/v1/auth/session");
    showApp();
  } catch (error) {
    if (error.status === 401) {
      showLogin();
      return;
    }
    showLogin("无法连接服务端，请稍后重试");
  }
}

function showLogin(message = "") {
  closeEvents();
  elements.bootView.hidden = true;
  elements.appView.hidden = true;
  elements.loginView.hidden = false;
  elements.loginError.textContent = message;
  elements.loginError.hidden = !message;
  window.setTimeout(() => elements.username.focus(), 0);
}

async function showApp() {
  elements.bootView.hidden = true;
  elements.loginView.hidden = true;
  elements.appView.hidden = false;
  try {
    const response = await api("/api/v1/devices");
    applySnapshot(response.devices || []);
    openEvents();
  } catch (error) {
    if (error.status === 401) {
      showLogin();
      return;
    }
    showToast(error.message || "无法读取设备列表");
  }
}

async function login(event) {
  event.preventDefault();
  elements.loginError.hidden = true;
  elements.loginButton.disabled = true;
  elements.loginButton.textContent = "登录中…";
  try {
    await api("/api/v1/auth/login", {
      method: "POST",
      body: { username: elements.username.value, password: elements.password.value },
    });
    elements.password.value = "";
    await showApp();
  } catch (error) {
    elements.loginError.textContent = error.message || "登录失败";
    elements.loginError.hidden = false;
    elements.password.focus();
  } finally {
    elements.loginButton.disabled = false;
    elements.loginButton.textContent = "登录";
  }
}

function togglePassword() {
  const visible = elements.password.type === "text";
  elements.password.type = visible ? "password" : "text";
  elements.togglePassword.setAttribute("aria-pressed", String(!visible));
  elements.togglePassword.setAttribute("aria-label", visible ? "显示密码" : "隐藏密码");
  elements.password.focus();
}

async function logout() {
  try {
    await api("/api/v1/auth/logout", { method: "POST" });
  } finally {
    state.devices = [];
    showLogin();
  }
}

function openEvents() {
  closeEvents();
  const source = new EventSource("/api/v1/events");
  source.addEventListener("snapshot", event => {
    const payload = JSON.parse(event.data);
    applySnapshot(payload.devices || []);
  });
  source.addEventListener("device.updated", event => {
    const device = JSON.parse(event.data);
    const index = state.devices.findIndex(item => item.id === device.id);
    if (index === -1) {
      state.devices.push(device);
    } else {
      state.devices[index] = device;
    }
    sortDevices();
    ensureSelection();
    render();
  });
  source.addEventListener("device.removed", event => {
    const payload = JSON.parse(event.data);
    state.devices = state.devices.filter(device => device.id !== payload.id);
    ensureSelection();
    render();
  });
  source.onerror = () => {
    if (source.readyState === EventSource.CLOSED) {
      showToast("状态连接已断开，请刷新页面重试");
    }
  };
  state.events = source;
}

function closeEvents() {
  if (state.events) {
    state.events.close();
    state.events = null;
  }
}

function applySnapshot(devices) {
  state.devices = devices;
  sortDevices();
  ensureSelection();
  render();
}

function sortDevices() {
  state.devices.sort((left, right) => {
    const created = String(left.createdAt).localeCompare(String(right.createdAt));
    return created || left.id.localeCompare(right.id);
  });
}

function ensureSelection() {
  if (state.devices.some(device => device.id === state.selectedId)) {
    return;
  }
  const fallback = state.devices.find(device => device.status === "online") || state.devices[0];
  state.selectedId = fallback ? fallback.id : null;
  writeSelectedDevice(state.selectedId);
}

function selectedDevice() {
  return state.devices.find(device => device.id === state.selectedId) || null;
}

function render() {
  const device = selectedDevice();
  const empty = !device;
  elements.deviceFocus.hidden = empty;
  elements.emptyState.hidden = !empty;
  elements.removeDevice.hidden = empty;
  elements.deviceRail.parentElement.hidden = empty;
  if (empty) {
    return;
  }

  elements.deviceName.textContent = device.name;
  elements.deviceStatus.textContent = device.status === "online" ? "在线" : "离线";
  elements.deviceStatus.dataset.status = device.status;
  const lastSeen = device.status === "online" ? "刚刚" : formatLastSeen(device.lastSeenAt);
  elements.deviceMeta.textContent = `${device.macAddress} · ${lastSeen}`;

  const isOnline = device.status === "online";
  const isBusy = state.busyDeviceId === device.id;
  elements.deviceAction.dataset.action = isOnline ? "shutdown" : "wake";
  elements.deviceActionLabel.textContent = isBusy ? "处理中…" : (isOnline ? "关机" : "唤醒");
  elements.deviceAction.disabled = isBusy;
  elements.deviceAction.setAttribute("aria-label", `${isOnline ? "关闭" : "唤醒"}${device.name}`);

  elements.deviceRail.replaceChildren(...state.devices.map(makeDeviceTab));
  const activeTab = elements.deviceRail.querySelector('[aria-selected="true"]');
  activeTab?.scrollIntoView({ behavior: "smooth", block: "nearest", inline: "center" });
}

function makeDeviceTab(device) {
  const button = document.createElement("button");
  button.type = "button";
  button.className = "device-tab";
  button.setAttribute("role", "tab");
  button.setAttribute("aria-selected", String(device.id === state.selectedId));
  button.setAttribute("aria-label", `${device.name}，${device.status === "online" ? "在线" : "离线"}`);

  const dot = document.createElement("span");
  dot.className = "device-tab__dot";
  dot.dataset.status = device.status;
  dot.setAttribute("aria-hidden", "true");
  const label = document.createElement("span");
  label.textContent = device.name;
  button.append(dot, label);
  button.addEventListener("click", () => {
    state.selectedId = device.id;
    writeSelectedDevice(device.id);
    render();
  });
  return button;
}

function confirmDeviceAction() {
  const device = selectedDevice();
  if (!device) return;
  if (device.status === "online") {
    showConfirm({
      title: `关闭“${device.name}”？`,
      message: "指令送达后，电脑将立即关机。",
      submitText: "确认关机",
      action: () => controlDevice(device, "shutdown"),
    });
  } else {
    showConfirm({
      title: `唤醒“${device.name}”？`,
      message: "服务端将向局域网发送 Wake-on-LAN 数据包。",
      submitText: "发送唤醒",
      danger: false,
      action: () => controlDevice(device, "wake"),
    });
  }
}

function confirmRemoveDevice() {
  const device = selectedDevice();
  if (!device) return;
  showConfirm({
    title: `移除“${device.name}”？`,
    message: "设备凭据将立即失效，重新使用时需要再次绑定。",
    submitText: "确认移除",
    action: () => removeDevice(device),
  });
}

function showConfirm({ title, message, submitText, danger = true, action }) {
  elements.confirmTitle.textContent = title;
  elements.confirmMessage.textContent = message;
  elements.confirmSubmit.textContent = submitText;
  elements.confirmSubmit.className = `button ${danger ? "button--danger" : "button--primary"}`;
  elements.confirmSubmit.disabled = false;
  state.confirmAction = action;
  elements.confirmDialog.showModal();
  elements.confirmCancel.focus();
}

async function runConfirmedAction() {
  if (!state.confirmAction) return;
  const action = state.confirmAction;
  state.confirmAction = null;
  elements.confirmSubmit.disabled = true;
  elements.confirmDialog.close();
  await action();
}

async function controlDevice(device, action) {
  state.busyDeviceId = device.id;
  render();
  try {
    await api(`/api/v1/devices/${encodeURIComponent(device.id)}/${action}`, { method: "POST" });
    showToast(action === "shutdown" ? "关机指令已送达" : "唤醒数据包已发送");
  } catch (error) {
    showToast(error.message || "操作失败");
  } finally {
    state.busyDeviceId = null;
    render();
  }
}

async function removeDevice(device) {
  state.busyDeviceId = device.id;
  render();
  try {
    await api(`/api/v1/devices/${encodeURIComponent(device.id)}`, { method: "DELETE" });
    state.devices = state.devices.filter(item => item.id !== device.id);
    ensureSelection();
    render();
    showToast("设备已移除");
  } catch (error) {
    showToast(error.message || "无法移除设备");
  } finally {
    state.busyDeviceId = null;
    render();
  }
}

async function createPairingToken() {
  elements.createToken.disabled = true;
  elements.emptyCreateToken.disabled = true;
  try {
    const response = await api("/api/v1/pairing-tokens", { method: "POST" });
    showToken(response.token, response.expiresAt);
  } catch (error) {
    showToast(error.message || "无法生成绑定 Token");
  } finally {
    elements.createToken.disabled = false;
    elements.emptyCreateToken.disabled = false;
  }
}

function showToken(token, expiresAt) {
  clearTokenDialog();
  elements.tokenValue.textContent = token;
  elements.copyToken.textContent = "复制";
  updateTokenCountdown(expiresAt);
  state.tokenTimer = window.setInterval(() => updateTokenCountdown(expiresAt), 1000);
  elements.tokenDialog.showModal();
  elements.copyToken.focus();
}

function updateTokenCountdown(expiresAt) {
  const remaining = Math.max(0, Math.ceil((new Date(expiresAt).getTime() - Date.now()) / 1000));
  const minutes = String(Math.floor(remaining / 60)).padStart(2, "0");
  const seconds = String(remaining % 60).padStart(2, "0");
  elements.tokenCountdown.textContent = remaining > 0 ? `${minutes}:${seconds} 后过期` : "Token 已过期";
  if (remaining === 0 && state.tokenTimer) {
    window.clearInterval(state.tokenTimer);
    state.tokenTimer = null;
  }
}

function clearTokenDialog() {
  if (state.tokenTimer) {
    window.clearInterval(state.tokenTimer);
    state.tokenTimer = null;
  }
  if (elements.tokenValue) elements.tokenValue.textContent = "";
}

async function copyToken() {
  const token = elements.tokenValue.textContent;
  if (!token) return;
  try {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(token);
    } else {
      const input = document.createElement("textarea");
      input.value = token;
      input.setAttribute("readonly", "");
      input.className = "clipboard-helper";
      document.body.append(input);
      input.select();
      if (!document.execCommand("copy")) throw new Error("copy command failed");
      input.remove();
    }
    elements.copyToken.textContent = "已复制";
    window.setTimeout(() => { elements.copyToken.textContent = "复制"; }, 1400);
  } catch {
    showToast("复制失败，请手动选择 Token");
  }
}

function formatLastSeen(value) {
  if (!value) return "从未上线";
  const date = new Date(value);
  const now = new Date();
  const delta = now.getTime() - date.getTime();
  if (delta < 60_000) return "刚刚";
  if (delta < 60 * 60_000) return `${Math.floor(delta / 60_000)} 分钟前`;

  const time = new Intl.DateTimeFormat("zh-CN", { hour: "2-digit", minute: "2-digit", hour12: false }).format(date);
  const startToday = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const startDate = new Date(date.getFullYear(), date.getMonth(), date.getDate());
  const days = Math.round((startToday - startDate) / 86_400_000);
  if (days === 0) return `今天 ${time}`;
  if (days === 1) return `昨天 ${time}`;
  return new Intl.DateTimeFormat("zh-CN", { month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit", hour12: false }).format(date);
}

function showToast(message) {
  window.clearTimeout(state.toastTimer);
  elements.toast.textContent = message;
  elements.toast.hidden = false;
  state.toastTimer = window.setTimeout(() => { elements.toast.hidden = true; }, 3600);
}

async function api(path, options = {}) {
  const init = { method: options.method || "GET", credentials: "same-origin", headers: {} };
  if (options.body !== undefined) {
    init.headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(options.body);
  }
  const response = await fetch(path, init);
  if (response.status === 204) return null;
  const contentType = response.headers.get("Content-Type") || "";
  const payload = contentType.includes("application/json") ? await response.json() : null;
  if (!response.ok) {
    const error = new Error(payload?.error?.message || `请求失败（${response.status}）`);
    error.status = response.status;
    error.code = payload?.error?.code;
    throw error;
  }
  return payload;
}

function readSelectedDevice() {
  try {
    return window.localStorage.getItem("wollet.selectedDeviceId");
  } catch {
    return null;
  }
}

function writeSelectedDevice(value) {
  try {
    if (value) window.localStorage.setItem("wollet.selectedDeviceId", value);
    else window.localStorage.removeItem("wollet.selectedDeviceId");
  } catch {
    // Selection persistence is optional; the app remains fully functional.
  }
}
