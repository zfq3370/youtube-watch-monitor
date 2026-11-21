const API = 'http://127.0.0.1:18964';
const ALARM = 'watch-scheduler';
let stateQueue = Promise.resolve();

async function serialize(action) {
  const previous = stateQueue;
  let release;
  stateQueue = new Promise(resolve => { release = resolve; });
  await previous;
  try { return await action(); } finally { release(); }
}

let readerTimer, pumping = false;

function scheduler(force = false) { return serialize(() => runScheduler(force)).finally(startReaderPump); }

function startReaderPump() {
  if (!readerTimer) readerTimer = setInterval(pumpReaders, 1500);
}

async function pumpReaders() {
  if (pumping) return;
  pumping = true;
  try {
    const { enabled, taskEntries = {} } = await chrome.storage.local.get(['enabled', 'taskEntries']);
    const running = enabled ? Object.values(taskEntries).filter(entry => entry.running && entry.tabId) : [];
    if (!running.length) { clearInterval(readerTimer); readerTimer = undefined; return; }
    for (const entry of running) {
      try { await chrome.tabs.sendMessage(entry.tabId, { type: 'monitor-tick', scanId: entry.scanId }); }
      catch { /* Content scripts are temporarily unavailable during navigation. */ }
    }
  } finally { pumping = false; }
}

async function request(path, body, code) {
  if (!code) code = (await chrome.storage.local.get('pairCode')).pairCode;
  if (!code) throw new Error('请先填写本机配对码');
  const response = await fetch(API + path, {
    method: body === undefined ? 'GET' : 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Monitor-Token': code },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    signal: AbortSignal.timeout(10000)
  });
  if (!response.ok) throw new Error(response.status === 401 ? '配对码错误' : `本机程序返回 ${response.status}，请确认两端均已升级`);
  return response.json();
}

function taskURL(task, resolvedURL) {
  const url = new URL(resolvedURL || task.url);
  url.searchParams.delete('qoderWatchTask');
  if (task.mode === 'comment') url.searchParams.set('lc', task.commentId);
  return url.href;
}

function samePage(actualURL, expectedURL) {
  try {
    const actual = new URL(actualURL || '');
    const expected = new URL(expectedURL);
    return actual.origin === 'https://www.youtube.com' && actual.origin === expected.origin &&
      actual.pathname === expected.pathname &&
      actual.searchParams.get('v') === expected.searchParams.get('v') &&
      actual.searchParams.get('list') === expected.searchParams.get('list');
  } catch { return false; }
}

function ownedTab(tab, task, entry) {
  return tab.id === entry.tabId && samePage(tab.url, entry.tabURL || taskURL(task, entry.resolvedURL));
}

async function setSummary(tasks, entries) {
  await chrome.storage.local.set({
    summary: tasks.map(task => ({
      id: task.id, name: task.name, mode: task.mode, intervalSeconds: task.intervalSeconds,
      status: entries[task.id]?.status || '等待检查', checkedAt: entries[task.id]?.checkedAt || ''
    }))
  });
}

async function runScheduler(force = false) {
  try {
    const saved = await chrome.storage.local.get(['enabled', 'taskEntries', 'forceCheck', 'tasks']);
    if (!saved.enabled) return;
    const config = await request('/tasks');
    if (config.version !== 2 || !Array.isArray(config.tasks)) throw new Error('请先启动新版多任务监控程序');
    const tasks = config.tasks.filter(task => task.enabled);
    const entries = saved.taskEntries || {};
    if (config.forceCheck !== saved.forceCheck) force = true;
    for (const id of Object.keys(entries)) {
      const configured = config.tasks.find(task => task.id === id);
      if (!configured) delete entries[id];
      else if (!configured.enabled) entries[id].running = false;
    }
    for (const task of tasks) {
      let entry = entries[task.id];
      if (!entry || entry.revision !== task.revision) {
        const previous = (saved.tasks || []).find(item => item.id === task.id);
        entry = {
          tabId: entry?.tabId,
          tabURL: entry?.tabURL || (previous ? taskURL(previous, entry?.resolvedURL) : undefined),
          resolvedURL: previous?.mode === task.mode && previous?.url === task.url ? entry?.resolvedURL : undefined,
          revision: task.revision, nextAt: 0, status: '任务配置已更新'
        };
        entries[task.id] = entry;
      }
      if (entry.running && Date.now() - entry.startedAt < 165000) continue;
      if (entry.running) {
        entry.running = false;
        entry.status = '页面超时，保留上次正常内容';
        await request('/observe', { taskId: task.id, revision: task.revision, scanId: entry.scanId, ok: false, error: entry.status });
      }
      if (!force && Date.now() < (entry.nextAt || 0)) continue;
      let tab;
      if (entry.tabId) { try { tab = await chrome.tabs.get(entry.tabId); } catch { /* A dedicated tab may have been closed. */ } }
      entry.scanId = crypto.randomUUID();
      entry.startedAt = Date.now();
      entry.running = true;
      entry.nextAt = Date.now() + task.intervalSeconds * 1000;
      entry.status = '正在加载页面…';
      entries[task.id] = entry;
      await chrome.storage.local.set({ taskEntries: entries, tasks });
      try {
        const targetURL = taskURL(task, entry.resolvedURL);
        if (!tab || !ownedTab(tab, task, entry)) {
          tab = await chrome.tabs.create({ url: targetURL, active: false });
          entry.tabId = tab.id;
        } else if (!samePage(tab.url, targetURL) ||
          (task.mode === 'comment' && new URL(tab.url).searchParams.get('lc') !== task.commentId)) {
          await chrome.tabs.update(tab.id, { url: targetURL });
        } else await chrome.tabs.reload(tab.id);
        entry.tabURL = targetURL;
        await chrome.tabs.update(tab.id, { muted: true });
      } catch (error) {
        entry.running = false;
        entry.status = `加载失败：${error.message}`;
        await request('/observe', { taskId: task.id, revision: task.revision, scanId: entry.scanId, ok: false, error: entry.status });
      }
      await chrome.storage.local.set({ taskEntries: entries });
    }
    const latest = (await chrome.storage.local.get('taskEntries')).taskEntries || entries;
    await setSummary(tasks, latest);
    await chrome.storage.local.set({ forceCheck: config.forceCheck, status: tasks.length ? `已同步 ${tasks.length} 项任务，按各自间隔检查` : '当前没有启用的任务，请在桌面程序中添加或启用' });
    await chrome.action.setBadgeText({ text: tasks.length ? String(tasks.length) : '0' });
    await chrome.action.setBadgeBackgroundColor({ color: '#167548' });
  } catch (error) {
    await chrome.storage.local.set({ status: `${error.message}（请确认桌面程序已启动）` });
    await chrome.action.setBadgeText({ text: '!' });
    await chrome.action.setBadgeBackgroundColor({ color: '#b45309' });
  }
}

async function senderTask(sender) {
  const saved = await chrome.storage.local.get(['enabled', 'tasks', 'taskEntries']);
  if (!saved.enabled || !sender.tab) return null;
  const task = (saved.tasks || []).find(item => saved.taskEntries?.[item.id]?.tabId === sender.tab.id);
  if (!task) return null;
  const entry = saved.taskEntries[task.id];
  return ownedTab(sender.tab, task, entry) && entry.running ? { task, entry, saved } : null;
}

chrome.runtime.onMessage.addListener((message, sender, respond) => {
  (async () => {
    if (['monitor-config', 'member-list', 'observation'].includes(message.type)) {
      return serialize(async () => {
      const match = await senderTask(sender);
      if (!match) return { ignored: true };
      const { task, entry } = match;
      if (message.type === 'monitor-config') return { task, scanId: entry.scanId };
      if (message.scanId !== entry.scanId) return { ignored: true };
      if (message.type === 'member-list') {
        const resolved = new URL(message.url);
        if (task.mode !== 'members' || resolved.origin !== 'https://www.youtube.com' || resolved.pathname !== '/playlist' || !/^UUMO[A-Za-z0-9_-]{20,}$/.test(resolved.searchParams.get('list') || '')) throw new Error('不是有效的会员专享列表');
        entry.resolvedURL = `https://www.youtube.com/playlist?list=${encodeURIComponent(resolved.searchParams.get('list'))}`;
        entry.tabURL = taskURL(task, entry.resolvedURL);
        const saved = await chrome.storage.local.get('taskEntries');
        saved.taskEntries[task.id] = entry;
        await chrome.storage.local.set({ taskEntries: saved.taskEntries });
        await chrome.tabs.update(sender.tab.id, { url: taskURL(task, entry.resolvedURL), muted: true });
        return { ok: true };
      }
      const payload = { ...message.payload, taskId: task.id, revision: task.revision, scanId: entry.scanId };
      const result = await request('/observe', payload);
      const saved = await chrome.storage.local.get('taskEntries');
      const current = saved.taskEntries?.[task.id];
      if (current?.scanId === entry.scanId) {
        current.running = false;
        current.checkedAt = new Date().toISOString();
        current.status = result.status;
        await chrome.storage.local.set({ taskEntries: saved.taskEntries, checkedAt: current.checkedAt });
        await setSummary(match.saved.tasks, saved.taskEntries);
      }
      return { ok: true };
      });
    }
    if (sender.tab || sender.id !== chrome.runtime.id) throw new Error('无效操作来源');
    if (message.type === 'start') {
      if (!/^[a-f0-9]{64}$/.test(message.pairCode || '')) throw new Error('请粘贴本机程序提供的完整配对码');
      const health = await request('/health', undefined, message.pairCode);
      if (health.version !== 2) throw new Error('请先启动新版多任务程序');
      await chrome.storage.local.set({ enabled: true, pairCode: message.pairCode.trim(), status: '已配对，正在同步任务…' });
      await chrome.alarms.create(ALARM, { periodInMinutes: 0.5 });
      await scheduler(true);
      return { ok: true };
    }
    if (message.type === 'stop') {
      await chrome.storage.local.set({ enabled: false, status: '全部监控已暂停' });
      await chrome.alarms.clear(ALARM);
      await chrome.action.setBadgeText({ text: '停' });
      return { ok: true };
    }
    if (message.type === 'check') { await scheduler(true); return { ok: true }; }
    throw new Error('未知操作');
  })().then(respond).catch(async error => {
    await chrome.storage.local.set({ status: error.message });
    respond({ error: error.message });
  });
  return true;
});

chrome.alarms.onAlarm.addListener(alarm => { if (alarm.name === ALARM) scheduler(); });

async function keepReaderReadable(tabId) {
  const { enabled, taskEntries = {}, tasks = [] } = await chrome.storage.local.get(['enabled', 'taskEntries', 'tasks']);
  if (!enabled) return;
  const owned = tasks.find(task => task.mode !== 'members' && task.enabled && taskEntries[task.id]?.tabId === tabId);
  if (!owned) return;
  try { await chrome.scripting.executeScript({ target: { tabId }, files: ['inject.js'], world: 'MAIN', injectImmediately: true }); } catch { /* The page may already be moving to the next navigation. */ }
}

chrome.webNavigation.onCommitted.addListener(details => { if (details.frameId === 0) keepReaderReadable(details.tabId); });
async function restore() {
  return serialize(async () => {
    await chrome.alarms.clear('pinned-watch');
    const { enabled, taskEntries = {} } = await chrome.storage.local.get(['enabled', 'taskEntries']);
    if (!enabled) return;
    for (const entry of Object.values(taskEntries)) entry.running = false;
    await chrome.storage.local.set({ taskEntries });
    await chrome.alarms.create(ALARM, { periodInMinutes: 0.5 });
    await runScheduler(true);
  }).finally(startReaderPump);
}
startReaderPump();
chrome.runtime.onStartup.addListener(restore);
chrome.runtime.onInstalled.addListener(restore);
