"use strict";

const state = {
  devices: [],
  selectedId: readSelectedDevice(),
  events: null,
  busyDeviceId: null,
  authEnabled: true,
  confirmAction: null,
  confirmTimer: null,
  planDialog: null,
  tokenTimer: null,
  tokenDeviceIds: null,
  toastTimer: null,
  relativeTimeTimer: null,
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
    deviceActivity: document.querySelector("#device-activity"),
    deviceAction: document.querySelector("#device-action"),
    deviceActionLabel: document.querySelector("#device-action-label"),
    deviceRail: document.querySelector("#device-rail"),
    removeDevice: document.querySelector("#remove-device"),
    tokenDialog: document.querySelector("#token-dialog"),
    tokenValue: document.querySelector("#token-value"),
    tokenCountdown: document.querySelector("#token-countdown"),
    copyToken: document.querySelector("#copy-token"),
    copyServer: document.querySelector("#copy-server"),
    confirmDialog: document.querySelector("#confirm-dialog"),
    confirmTitle: document.querySelector("#confirm-title"),
    confirmMessage: document.querySelector("#confirm-message"),
    confirmCountdown: document.querySelector("#confirm-countdown"),
    confirmCancel: document.querySelector("#confirm-cancel"),
    confirmSubmit: document.querySelector("#confirm-submit"),
    toast: document.querySelector("#toast"),
  });

  bindInteractions();
  bootstrap();
});

function bindInteractions() {
  document.addEventListener("keydown", event => {
    if (event.key === "Tab") document.body.dataset.keyboardNavigation = "true";
  });
  document.addEventListener("pointerdown", () => {
    delete document.body.dataset.keyboardNavigation;
  });
  elements.loginForm.addEventListener("submit", login);
  elements.togglePassword.addEventListener("click", togglePassword);
  elements.createToken.addEventListener("click", createPairingToken);
  elements.emptyCreateToken.addEventListener("click", createPairingToken);
  elements.logout.addEventListener("click", logout);
  elements.deviceAction.addEventListener("click", confirmDeviceAction);
  elements.removeDevice.addEventListener("click", confirmRemoveDevice);
  elements.copyToken.addEventListener("click", copyToken);
  elements.copyServer.addEventListener("click", copyServerAddress);
  elements.tokenDialog.addEventListener("close", clearTokenDialog);
  elements.confirmDialog.addEventListener("close", clearConfirmDialog);
  elements.confirmCancel.addEventListener("click", () => {
    if (state.planDialog && state.planDialog.active) void sendPlanAction("cancel");
    else elements.confirmDialog.close();
  });
  elements.confirmDialog.addEventListener("cancel", event => {
    if (state.planDialog?.active) { event.preventDefault(); void sendPlanAction("cancel"); }
  });
  elements.confirmSubmit.addEventListener("click", runConfirmedAction);
  state.relativeTimeTimer = window.setInterval(refreshSelectedDeviceMeta, 30_000);
}

async function bootstrap() {
  try {
    const session = await api("/api/v1/auth/session");
    state.authEnabled = session.authenticationEnabled !== false;
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
  elements.logout.hidden = !state.authEnabled;
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
    const session = await api("/api/v1/auth/login", {
      method: "POST",
      body: { username: elements.username.value, password: elements.password.value },
    });
    state.authEnabled = session.authenticationEnabled !== false;
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
  if (!state.authEnabled) return;
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
    applyDeviceUpdate(JSON.parse(event.data));
  });
  source.addEventListener("device.wake_timeout", event => {
    const device = JSON.parse(event.data);
    applyDeviceUpdate(device);
    showToast(`仍未检测到“${device.name}”上线，可以再次尝试唤醒`);
  });
  source.addEventListener("device.shutdown_timeout", event => {
    const device = JSON.parse(event.data);
    applyDeviceUpdate(device);
    showToast(`“${device.name}”仍然在线，可以再次尝试关机`);
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
  devices.forEach(observePlan);
  const pairedDevice = findNewlyPairedDevice(devices);
  state.devices = devices;
  sortDevices();
  ensureSelection();
  render();
  closeTokenDialogForDevice(pairedDevice);
}

function applyDeviceUpdate(device) {
  observePlan(device);
  const index = state.devices.findIndex(item => item.id === device.id);
  const isNew = index === -1;
  if (isNew) {
    state.devices.push(device);
  } else {
    state.devices[index] = device;
  }
  sortDevices();
  ensureSelection();
  render();
  closeTokenDialogForDevice(isNew ? device : null);
}

function findNewlyPairedDevice(devices) {
  if (!elements.tokenDialog.open || !state.tokenDeviceIds) return null;
  return devices.find(device => !state.tokenDeviceIds.has(device.id)) || null;
}

function closeTokenDialogForDevice(device) {
  if (!device || !elements.tokenDialog.open || !state.tokenDeviceIds || state.tokenDeviceIds.has(device.id)) return;
  elements.tokenDialog.close();
  showToast(`设备“${device.name}”已绑定`);
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

  const presentation = devicePresentation(device);
  elements.deviceName.textContent = device.name;
  elements.deviceStatus.textContent = presentation.statusText;
  elements.deviceStatus.dataset.status = presentation.statusKey;
  renderDeviceMeta(device);

  const isOnline = device.status === "online";
  const isBusy = state.busyDeviceId === device.id;
  elements.deviceAction.dataset.action = isOnline ? "shutdown" : "wake";
  if (device.operation) {
    elements.deviceAction.dataset.operation = device.operation;
  } else {
    delete elements.deviceAction.dataset.operation;
  }
  elements.deviceActionLabel.textContent = isBusy ? "处理中…" : presentation.actionText;
  elements.deviceAction.disabled = isBusy;
  elements.deviceAction.setAttribute("aria-label", `${presentation.actionText}${device.name}，当前${presentation.statusText}`);

  elements.deviceRail.replaceChildren(...state.devices.map(makeDeviceTab));
  const activeTab = elements.deviceRail.querySelector('[aria-selected="true"]');
  activeTab?.scrollIntoView({ behavior: "smooth", block: "nearest", inline: "center" });
}

function devicePresentation(device) {
  if (device.shutdownPlan?.state === "scheduled") {
    return { statusKey: "shutting_down", statusText: "关机倒计时", actionText: "查看关机" };
  }
  if (device.status === "online" && ["executing", "submitted"].includes(device.shutdownPlan?.state)) {
    return { statusKey: "shutting_down", statusText: "关机中", actionText: "查看状态" };
  }
  if (device.operation === "waking") {
    return { statusKey: "waking", statusText: "开机中", actionText: "再次唤醒" };
  }
  if (device.operation === "shutting_down") {
    return { statusKey: "shutting_down", statusText: "关机中", actionText: "再次关机" };
  }
  if (device.status === "online") {
    return { statusKey: "online", statusText: "在线", actionText: "关机" };
  }
  return { statusKey: "offline", statusText: "离线", actionText: "唤醒" };
}

function renderDeviceMeta(device) {
  let activity = "连接正常";
  if (device.operation === "waking") {
    activity = "等待上线";
  } else if (device.operation === "shutting_down") {
    activity = "等待离线";
  } else if (device.status !== "online") {
    const lastSeen = formatLastSeen(device.lastSeenAt);
    activity = lastSeen === "从未上线" ? lastSeen : `最后在线 ${lastSeen}`;
  }
  elements.deviceMeta.textContent = device.macAddress;
  elements.deviceActivity.textContent = activity;
}

function refreshSelectedDeviceMeta() {
  const device = selectedDevice();
  if (device && !elements.deviceFocus.hidden) renderDeviceMeta(device);
}

function makeDeviceTab(device) {
  const presentation = devicePresentation(device);
  const button = document.createElement("button");
  button.type = "button";
  button.className = "device-tab";
  button.setAttribute("role", "tab");
  button.setAttribute("aria-selected", String(device.id === state.selectedId));
  button.setAttribute("aria-label", `${device.name}，${presentation.statusText}`);

  const dot = document.createElement("span");
  dot.className = "device-tab__dot";
  dot.dataset.status = presentation.statusKey;
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
  if (device.capabilities?.includes("shutdown-plan.v1") || device.shutdownPlan?.state === "scheduled") {
    if (device.status === "online" || device.shutdownPlan?.state === "scheduled") {
      void showPlanDialog(device);
      return;
    }
  }
  if (device.status === "online") {
    showShutdownCountdown(device);
  } else if (device.operation !== "waking") {
    void controlDevice(device, "wake");
  } else {
    showConfirm({
      title: `再次唤醒“${device.name}”？`,
      message: "已经发送过唤醒请求，设备仍未上线。可以再次发送 Wake-on-LAN 数据包。",
      submitText: "再次发送",
      danger: false,
      action: () => controlDevice(device, "wake"),
    });
  }
}

function showShutdownCountdown(device) {
  const repeated = device.operation === "shutting_down";
  showConfirm({
    title: `${repeated ? "再次" : ""}关闭“${device.name}”？`,
    message: repeated
      ? "关机指令已经送达，但设备仍在线。可以取消，或再次发送关机指令。"
      : "关机指令将在倒计时结束后发送。可以取消，或立即关机。",
    submitText: "立即关机",
    action: () => controlDevice(device, "shutdown"),
  });

  const deadline = Date.now() + 10_000;
  elements.confirmCountdown.hidden = false;
  updateShutdownCountdown(deadline);
  state.confirmTimer = window.setInterval(() => updateShutdownCountdown(deadline), 250);
}

function updateShutdownCountdown(deadline) {
  const remaining = Math.max(0, Math.ceil((deadline - Date.now()) / 1000));
  elements.confirmCountdown.textContent = `${remaining} 秒`;
  if (remaining > 0) return;

  const action = state.confirmAction;
  state.confirmAction = null;
  elements.confirmDialog.close();
  if (action) void action();
}

function confirmRemoveDevice() {
  const device = selectedDevice();
  if (!device) return;
  showConfirm({
    title: `移除“${device.name}”？`,
    message: "设备凭据将立即失效，重新使用时需要再次绑定。已接受的关机计划可能继续执行，请先取消关机。",
    submitText: "确认移除",
    action: () => removeDevice(device),
  });
}

function showConfirm({ title, message, submitText, danger = true, action }) {
  clearConfirmDialog();
  elements.confirmTitle.textContent = title;
  elements.confirmMessage.textContent = message;
  elements.confirmSubmit.textContent = submitText;
  elements.confirmSubmit.className = `button ${danger ? "button--danger" : "button--primary"}`;
  elements.confirmSubmit.disabled = false;
  state.confirmAction = action;
  elements.confirmDialog.showModal();
  elements.confirmCancel.focus();
}

function clearConfirmDialog() {
  if (state.confirmTimer) {
    window.clearInterval(state.confirmTimer);
    state.confirmTimer = null;
  }
  state.confirmAction = null;
  state.planDialog = null;
  if (elements.confirmCancel) { elements.confirmCancel.textContent = "取消"; elements.confirmCancel.disabled = false; }
  if (elements.confirmCountdown) {
    elements.confirmCountdown.hidden = true;
    elements.confirmCountdown.textContent = "";
  }
}

async function runConfirmedAction() {
  if (state.planDialog) { await sendPlanAction("execute"); return; }
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
    const response = await api(`/api/v1/devices/${encodeURIComponent(device.id)}/${action}`, { method: "POST" });
    const current = state.devices.find(item => item.id === device.id);
    if (current && response?.operation) {
      current.operation = response.operation;
      render();
    }
    showToast(action === "shutdown" ? "关机指令已送达，等待设备离线" : "唤醒数据包已发送，等待设备上线");
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
    showToken(response.token, response.expiresAt, response.expiresInSeconds);
  } catch (error) {
    showToast(error.message || "无法生成绑定 Token");
  } finally {
    elements.createToken.disabled = false;
    elements.emptyCreateToken.disabled = false;
  }
}

function showToken(token, expiresAt, expiresInSeconds) {
  clearTokenDialog();
  state.tokenDeviceIds = new Set(state.devices.map(device => device.id));
  elements.tokenValue.textContent = token;
  elements.copyToken.textContent = "复制 Token";
  elements.copyServer.textContent = "复制服务器地址";
  const ttlSeconds = Number(expiresInSeconds);
  const deadline = Number.isFinite(ttlSeconds) && ttlSeconds > 0
    ? Date.now() + ttlSeconds * 1000
    : new Date(expiresAt).getTime();
  updateTokenCountdown(deadline);
  state.tokenTimer = window.setInterval(() => updateTokenCountdown(deadline), 1000);
  elements.tokenDialog.showModal();
  elements.copyToken.focus();
}

function updateTokenCountdown(deadline) {
  const remaining = Math.max(0, Math.ceil((deadline - Date.now()) / 1000));
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
  state.tokenDeviceIds = null;
  if (elements.tokenValue) elements.tokenValue.textContent = "";
}

async function copyToken() {
  const token = elements.tokenValue.textContent;
  if (!token) return;
  await copyValue(token, elements.copyToken, "复制 Token", "复制失败，请手动选择 Token");
}

async function copyServerAddress() {
  await copyValue(window.location.origin, elements.copyServer, "复制服务器地址", "服务器地址复制失败");
}

async function copyValue(value, button, defaultText, failureMessage) {
  try {
    await writeClipboard(value);
    button.textContent = "已复制";
    window.setTimeout(() => { button.textContent = defaultText; }, 1400);
  } catch {
    showToast(failureMessage);
  }
}

async function writeClipboard(value) {
  if (navigator.clipboard?.writeText && window.isSecureContext) {
    try {
      await navigator.clipboard.writeText(value);
      return;
    } catch {
      // HTTP LAN deployments and browser permissions may reject the modern API.
    }
  }

  const input = document.createElement("textarea");
  input.value = value;
  input.readOnly = true;
  input.className = "clipboard-helper";
  (elements.tokenDialog.open ? elements.tokenDialog : document.body).append(input);
  try {
    input.focus();
    input.select();
    input.setSelectionRange(0, input.value.length);
    if (!document.execCommand("copy")) throw new Error("copy command failed");
  } finally {
    input.remove();
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
    error.payload = payload;
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

// A monotonic display estimate only. The client service owns the execution deadline.
function observePlan(device) {
  device.planObserved = performance.now();
  device.planRemaining = Math.max(0, new Date(device.shutdownPlan?.estimatedExecuteAt).getTime() - new Date(device.serverTime).getTime());
}

function newRequestId() {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 15) | 64;
  bytes[8] = (bytes[8] & 63) | 128;
  const hex = Array.from(bytes, value => value.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0,8)}-${hex.slice(8,12)}-${hex.slice(12,16)}-${hex.slice(16,20)}-${hex.slice(20)}`;
}

async function showPlanDialog(device) {
  const active = ["scheduled", "executing", "submitted"].includes(device.shutdownPlan?.state);
  const unknown = ["pending", "outcome_unknown"].includes(device.shutdownRequest?.status);
  const operationId = active ? device.shutdownPlan.operationId : unknown ? device.shutdownRequest.operationId : newRequestId();
  showConfirm({title: `关闭“${device.name}”`, message: "正在向客户端创建关机计划…", submitText: "立即关机"});
  const dialog = state.planDialog = {deviceId: device.id, operationId, creating: !active && !unknown, busy: false, active: false, querying: false};
  state.confirmTimer = window.setInterval(() => {
    updatePlanDialog();
    if (dialog === state.planDialog && !dialog.creating && !dialog.busy && !dialog.querying && performance.now() - (dialog.lastQuery || 0) > 2000) void queryPlan(dialog);
  }, 100);
  updatePlanDialog();
  if (!active && !unknown) {
    try {
      const payload = await api(`/api/v1/devices/${encodeURIComponent(device.id)}/shutdown-plans`, {method: "POST", body: {operationId}});
      applyPlanResponse(dialog, payload);
    } catch (error) {
      if (error.payload?.request) applyPlanResponse(dialog, error.payload);
      else dialog.error = error.message || "创建结果未确认，正在查询";
    } finally { dialog.creating = false; updatePlanDialog(); }
  }
}

function applyPlanResponse(dialog, payload) {
  const device = state.devices.find(item => item.id === dialog.deviceId);
  if (!device) return;
  const old = device.shutdownPlan;
  if (payload.plan && (!old || old.operationId !== payload.plan.operationId || payload.plan.revision >= old.revision)) {
    device.shutdownPlan = payload.plan;
    device.serverTime = payload.serverTime;
    observePlan(device);
  }
  device.shutdownRequest = payload.request;
  const unknown = ["pending", "outcome_unknown"].includes(payload.request?.status);
  dialog.error = payload.request?.status === "rejected" ? `操作未成功（${payload.request.code || "客户端拒绝"}）` : unknown ? "操作结果尚未确认，正在查询；客户端可能仍会关机。" : null;
  if (dialog.pendingRequest?.requestId === payload.request?.requestId && !unknown) dialog.pendingRequest = null;
  render();
  updatePlanDialog();
}

async function queryPlan(dialog) {
  dialog.querying = true;
  dialog.lastQuery = performance.now();
  try {
    const suffix = dialog.requestId ? `?requestId=${encodeURIComponent(dialog.requestId)}` : "";
    const payload = await api(`/api/v1/devices/${encodeURIComponent(dialog.deviceId)}/shutdown-plans/${dialog.operationId}${suffix}`);
    applyPlanResponse(dialog, payload);
  } catch (error) {
    if (error.status !== 404) dialog.error = "状态未同步，正在重新查询";
  } finally { dialog.querying = false; }
}

function updatePlanDialog() {
  const dialog = state.planDialog;
  if (!dialog) return;
  const device = state.devices.find(item => item.id === dialog.deviceId);
  const plan = device?.shutdownPlan?.operationId === dialog.operationId ? device.shutdownPlan : null;
  const fresh = plan?.synchronized && performance.now() - device.planObserved < 3000;
  dialog.active = plan?.state === "scheduled";
  elements.confirmCancel.textContent = dialog.active ? "取消关机" : "关闭";
  elements.confirmCancel.disabled = dialog.busy;
  elements.confirmSubmit.disabled = dialog.busy || !dialog.active || !fresh;
  elements.confirmCountdown.hidden = !dialog.active;
  if (dialog.active) {
    const remaining = Math.max(0, Math.ceil((device.planRemaining - (performance.now() - device.planObserved)) / 1000));
    elements.confirmCountdown.textContent = fresh && remaining > 0 ? `${remaining} 秒` : "等待客户端状态";
    elements.confirmMessage.textContent = dialog.error || (fresh ? "客户端正在倒计时。关闭此页面后仍会按时关机。" : "状态未同步，客户端可能仍会关机；可尝试取消或在客户端操作。");
  } else {
    elements.confirmMessage.textContent = dialog.error || ({executing: "客户端正在执行关机", submitted: "关机请求已提交给系统，等待设备离线", cancelled: "关机已取消", failed: "关机失败，请检查客户端", indeterminate: "执行结果无法确定，请检查客户端"}[plan?.state]) || "请求结果尚未确认，正在查询；请勿重复创建计划。";
  }
}

async function sendPlanAction(action) {
  const dialog = state.planDialog;
  if (!dialog || dialog.busy) return;
  const device = state.devices.find(item => item.id === dialog.deviceId);
  const plan = device?.shutdownPlan;
  if (plan?.operationId !== dialog.operationId || plan.state !== "scheduled") return;
  if (dialog.pendingRequest && dialog.pendingRequest.action !== action) {
    dialog.error = "上次操作结果尚未确认，请等待查询结果或在客户端操作。";
    updatePlanDialog();
    return;
  }
  dialog.busy = true;
  dialog.pendingRequest ||= {action, requestId: newRequestId(), revision: plan.revision};
  dialog.requestId = dialog.pendingRequest.requestId;
  updatePlanDialog();
  try {
    const payload = await api(`/api/v1/devices/${encodeURIComponent(dialog.deviceId)}/shutdown-plans/${dialog.operationId}/${action}`, {
      method: "POST", body: {requestId: dialog.requestId, expectedRevision: dialog.pendingRequest.revision},
    });
    applyPlanResponse(dialog, payload);
  } catch (error) {
    if (error.payload?.request) applyPlanResponse(dialog, error.payload);
    else dialog.error = error.message || "操作结果未确认，正在查询";
  } finally { dialog.busy = false; updatePlanDialog(); }
}
