const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const http = require('node:http');
const os = require('node:os');
const root = __dirname;
const read = name => fs.readFileSync(path.join(root, 'extension', name), 'utf8');
const clone = value => value === undefined ? undefined : JSON.parse(JSON.stringify(value));
const playlist = 'https://www.youtube.com/playlist?list=UUMOsampleMemberList000000';
const makeTask = (id, mode = 'pinned', intervalSeconds = 120) => ({ id, revision: 'revision-' + id, name: id, mode, intervalSeconds, enabled: true, url: mode === 'members' ? 'https://www.youtube.com/@samplechannel' : 'https://www.youtube.com/watch?v=TESTVIDEO01', author: '@samplechannel', commentId: 'UgsyntheticComment00000001' });

async function testBackground() {
  const state = {}, calls = [], tabs = new Map(), windows = new Map(), reports = [];
  let handler, startup, alarm, pump, committed, now = 1000000, nextId = 10, offline = false;
  const config = { version: 2, forceCheck: 0, tasks: [makeTask('pin'), makeTask('exact', 'comment', 300), makeTask('member', 'members', 180)] };
  const chrome = {
    storage: { local: { get: async keys => { const result = {}; for (const key of typeof keys === 'string' ? [keys] : keys) result[key] = clone(state[key]); return result; }, set: async values => Object.assign(state, clone(values)) } },
    tabs: {
      get: async id => { if (!tabs.has(id)) throw Error('closed'); return clone(tabs.get(id)); },
      create: async options => { const tab = { id: nextId++, windowId: 1, url: options.url }; tabs.set(tab.id, tab); calls.push(['create', tab.id, options]); return clone(tab); },
      sendMessage: async (id, message) => calls.push(['tick', id, clone(message)]),
      reload: async id => calls.push(['reload', id]),
      update: async (id, options) => { if (options.url) tabs.get(id).url = options.url; calls.push(['update', id, options]); return clone(tabs.get(id)); }
    },
    windows: {
      create: async options => { const win = { id: nextId++, state: 'normal' }; windows.set(win.id, win); tabs.get(options.tabId).windowId = win.id; calls.push(['window', win.id, options]); return clone(win); },
      get: async id => clone(windows.get(id)),
      update: async (id, options) => { Object.assign(windows.get(id), options); calls.push(['window-update', id, options]); }
    },
    alarms: { create: async (name, options) => calls.push(['alarm', name, options]), clear: async name => calls.push(['clear', name]), onAlarm: { addListener: cb => { alarm = cb; } } },
    webNavigation: { onCommitted: { addListener: cb => { committed = cb; } } },
    scripting: { executeScript: async options => calls.push(['inject', options.target.tabId, options.world, options.injectImmediately]) },
    action: { setBadgeText: async () => {}, setBadgeBackgroundColor: async () => {} },
    runtime: { id: 'unit-test', onMessage: { addListener: cb => { handler = cb; } }, onStartup: { addListener: cb => { startup = cb; } }, onInstalled: { addListener() {} } }
  };
  const ctx = vm.createContext({ chrome, URL, AbortSignal, crypto, setInterval: cb => { pump = cb; return 1; }, clearInterval: () => { pump = undefined; }, Date: class extends Date { constructor(...args) { super(...(args.length ? args : [now])); } static now() { return now; } }, fetch: async (url, options) => {
    if (offline) throw Error('offline'); assert.equal(options.headers['X-Monitor-Token'], 'b'.repeat(64));
    if (url.endsWith('/observe')) reports.push(JSON.parse(options.body));
    return { ok: true, json: async () => url.endsWith('/tasks') ? clone(config) : { version: 2, status: 'accepted' } };
  } });
  vm.runInContext(read('background.js'), ctx);
  const send = (message, sender = { id: 'unit-test' }) => new Promise(resolve => handler(message, sender, resolve));
  const senderFor = id => ({ id: 'unit-test', tab: clone(tabs.get(state.taskEntries[id].tabId)) });
  const finish = async id => send({ type: 'observation', scanId: state.taskEntries[id].scanId, payload: { ok: true, comment: { id: 'UgsyntheticComment00000001', text: 'body' } } }, senderFor(id));
  assert.ok((await send({ type: 'start', pairCode: '' })).error);
  assert.equal((await send({ type: 'start', pairCode: 'b'.repeat(64) })).ok, true);
  assert.equal(tabs.size, 3); assert.equal(state.summary.length, 3);
  await pump(); assert.equal(calls.filter(c => c[0] === 'tick').length, 3, 'background drives scans without page timers');
  assert.ok(calls.filter(c => c[0] === 'tick').every(c => c[2].scanId));
  assert.ok(calls.filter(c => c[0] === 'create').every(c => c[2].active === false));
  assert.equal(calls.filter(c => c[0] === 'update' && c[2].muted).length, 3);
  assert.equal(new URL(tabs.get(state.taskEntries.exact.tabId).url).searchParams.get('lc'), config.tasks[1].commentId);
  const before = calls.length; await send({ type: 'check' }); assert.equal(calls.length, before, 'in-flight scan not reloaded');
  assert.equal((await send({ type: 'monitor-config' }, senderFor('pin'))).task.id, 'pin');
  assert.equal((await send({ type: 'observation', scanId: 'wrong', payload: {} }, senderFor('pin'))).ignored, true);
  assert.equal((await send({ type: 'stop' }, senderFor('pin'))).error, '无效操作来源');
  assert.equal((await send({ type: 'member-list', scanId: state.taskEntries.member.scanId, url: 'https://www.youtube.com/playlist?list=PLordinary' }, senderFor('member'))).error, '不是有效的会员专享列表');
  assert.equal((await send({ type: 'member-list', scanId: state.taskEntries.member.scanId, url: playlist }, senderFor('member'))).ok, true);
  assert.equal(new URL(tabs.get(state.taskEntries.member.tabId).url).pathname, '/playlist');
  await Promise.all(['pin', 'exact', 'member'].map(finish));
  assert.ok(Object.values(state.taskEntries).every(e => !e.running && e.status === 'accepted'), 'concurrent reports preserved');
  await pump(); assert.equal(pump, undefined, 'reader pump stops when no scans are pending');
  now += 121000; await vm.runInContext('scheduler()', ctx);
  assert.equal(state.taskEntries.pin.running, true); assert.equal(state.taskEntries.exact.running, false); assert.equal(state.taskEntries.member.running, false);
  await finish('pin');
  const originalTabs = Object.fromEntries(Object.entries(state.taskEntries).map(([id, entry]) => [id, entry.tabId]));
  assert.equal(windows.size, 0, 'monitoring never opens an extra window');
  assert.ok(!calls.some(c => c[0] === 'update' && c[2].active), 'reader tabs stay in the background');
  assert.ok([...tabs.values()].every(tab => !new URL(tab.url).searchParams.has('qoderWatchTask')), 'clean YouTube URLs need no task marker');
  const settle = () => new Promise(resolve => setTimeout(resolve, 5));
  await committed({ tabId: state.taskEntries.pin.tabId, frameId: 0 }); await settle();
  assert.ok(calls.some(c => c[0] === 'inject' && c[1] === state.taskEntries.pin.tabId && c[2] === 'MAIN' && c[3] === true), 'comment tabs are patched before page scripts run');
  await committed({ tabId: state.taskEntries.member.tabId, frameId: 0 });
  await committed({ tabId: state.taskEntries.exact.tabId, frameId: 1 }); await settle();
  assert.equal(calls.filter(c => c[0] === 'inject').length, 1, 'playlist tabs and subframes stay untouched');
  assert.equal((await send({ type: 'monitor-config' }, { id: 'unit-test', tab: { id: 9999, url: config.tasks[0].url } })).ignored, true, 'ordinary matching page is not a monitoring tab');
  for (let cycle = 0; cycle < 12; cycle++) {
    now += 301000; await vm.runInContext('scheduler()', ctx);
    assert.equal(tabs.size, 3, 'repeated scans do not create more tabs');
    assert.equal(windows.size, 0, 'repeated scans still open no window');
    for (const id of ['pin', 'exact', 'member']) assert.equal(state.taskEntries[id].tabId, originalTabs[id], 'fixed tab per task');
    await Promise.all(['pin', 'exact', 'member'].map(finish));
  }
  config.tasks[2].revision = 'member-settings-change';
  await vm.runInContext('scheduler()', ctx);
  assert.equal(state.taskEntries.member.tabId, originalTabs.member);
  assert.equal(new URL(tabs.get(originalTabs.member).url).pathname, '/playlist', 'settings edits retain resolved member playlist');
  await finish('member');
  config.tasks[0].enabled = false; await vm.runInContext('scheduler()', ctx);
  assert.equal(state.taskEntries.pin.tabId, originalTabs.pin, 'paused task retains its dedicated tab');
  config.tasks[0].enabled = true; await send({ type: 'check' });
  assert.equal(tabs.size, 3, 'resuming tasks reuses their tabs');
  await Promise.all(['pin', 'exact', 'member'].map(finish));
  const strippedExact = new URL(tabs.get(originalTabs.exact).url); strippedExact.searchParams.delete('lc'); tabs.get(originalTabs.exact).url = strippedExact.href;
  await send({ type: 'check' });
  assert.equal(state.taskEntries.exact.tabId, originalTabs.exact);
  assert.equal(new URL(tabs.get(originalTabs.exact).url).searchParams.get('lc'), config.tasks[1].commentId, 'comment permalink is restored in the same tab');
  await Promise.all(['pin', 'exact', 'member'].map(finish));
  config.tasks[1].url = 'https://www.youtube.com/watch?v=BBBBBBBBBBB'; config.tasks[1].revision = 'exact-new-target';
  await vm.runInContext('scheduler()', ctx);
  assert.equal(state.taskEntries.exact.tabId, originalTabs.exact, 'editing target navigates the existing dedicated tab');
  assert.equal(new URL(tabs.get(originalTabs.exact).url).searchParams.get('v'), 'BBBBBBBBBBB');
  await finish('exact');
  tabs.delete(originalTabs.exact); await send({ type: 'check' });
  assert.notEqual(state.taskEntries.exact.tabId, originalTabs.exact, 'closed tab gets one replacement');
  assert.equal(tabs.size, 3);
  await Promise.all(['pin', 'exact', 'member'].map(finish));
  const pinTab = state.taskEntries.pin.tabId; tabs.get(pinTab).url = 'https://www.youtube.com/watch?v=AAAAAAAAAAA';
  const safeStart = calls.length; await send({ type: 'check' }); assert.notEqual(state.taskEntries.pin.tabId, pinTab); assert.ok(!calls.slice(safeStart).some(c => c[0] === 'reload' && c[1] === pinTab));
  now += 166000; await vm.runInContext('scheduler()', ctx); assert.ok(reports.some(r => r.ok === false && /超时/.test(r.error)));
  offline = true; const off = await finish('pin'); assert.match(off.error, /offline/); assert.equal(state.taskEntries.pin.running, true, 'failed bridge preserves pending scan'); offline = false;
  await finish('pin'); config.tasks[0].revision = 'new-revision'; await vm.runInContext('scheduler()', ctx); assert.equal(state.taskEntries.pin.revision, 'new-revision');
  const restartTabIds = Object.values(state.taskEntries).map(entry => entry.tabId);
  const restartScan = state.taskEntries.pin.scanId;
  await startup(); assert.ok(calls.some(c => c[0] === 'clear' && c[1] === 'pinned-watch')); assert.ok(alarm);
  assert.deepEqual(Object.values(state.taskEntries).map(entry => entry.tabId), restartTabIds, 'extension restart retains dedicated tab IDs');
  assert.notEqual(state.taskEntries.pin.scanId, restartScan, 'extension restart discards interrupted scan and refreshes immediately');
  await send({ type: 'stop' }); const stopped = calls.length; await send({ type: 'check' }); assert.equal(calls.length, stopped); assert.equal(state.enabled, false);
  console.log('PASS scheduler: 12 refresh cycles with fixed background tab IDs, no extra windows, hidden-page patch injection, clean URLs, settings/target edits, pause/resume, closed-tab replacement, user-tab protection, multi-task intervals, serialized reports, timeout, offline, restart');
}

function testReaders() {
  let cards = [], threads = [], containers = [], loaded = true;
  const comment = (id, text, pinned = false, emoji = '') => ({ querySelector(selector) {
    if (selector.startsWith('a[href')) return { getAttribute: () => '/watch?v=TESTVIDEO01&lc=' + id };
    if (selector === '#content-text') return { cloneNode: () => { const copy = { textContent: text, querySelectorAll: () => emoji ? [{ alt: emoji, replaceWith: replacement => { copy.textContent += replacement; } }] : [] }; return copy; } };
    if (selector.startsWith('a#name')) return { textContent: '示例博主', getAttribute: () => '/@samplechannel' };
    if (selector.startsWith('#pinned')) return pinned ? { textContent: '已由作者置顶' } : null;
    return null;
  } });
  const card = (id, index, badge = '会员专享', title = '会员标题') => ({ querySelectorAll: () => badge ? [{ textContent: badge }] : [], querySelector: () => ({ textContent: title, getAttribute: () => `/watch?v=${id}&index=${index}` }) });
  const ctx = vm.createContext({ URL, location: { href: playlist }, document: { createTextNode: value => value, querySelector: () => loaded ? {} : null, querySelectorAll: selector => selector.startsWith('yt-lockup') ? cards : selector.startsWith('a[href') ? [{ getAttribute: () => playlist }] : selector.includes('ytd-comment-view') ? containers : threads } });
  vm.runInContext(read('reader.js'), ctx); const readers = ctx.WatchReaders;
  threads = [comment('UgsyntheticComment00000001', ' old  body ', true, 'symbol')]; containers = threads;
  assert.equal(readers.comments(makeTask('p')).comment.text, 'old body symbol');
  assert.equal(readers.comments(makeTask('p')).comment.authorPath, '/@samplechannel');
  assert.equal(readers.comments(makeTask('e', 'comment')).ready, true);
  assert.equal(readers.comments({ mode: 'comment', commentId: 'missing' }).ready, false);
  threads = [comment('Ugordinary12345', 'ordinary')]; assert.equal(readers.comments(makeTask('p')).comment, null);
  loaded = false; assert.equal(readers.comments(makeTask('p')).ready, false); loaded = true;
  assert.equal(readers.memberList(), playlist);
  cards = [card('BBBBBBBBBBB', 2), card('AAAAAAAAAAA', 1), card('PPPPPPPPPPP', 3, ''), card('CCCCCCCCCCC', 4, '公开'), card('DDDDDDDDDDD', 5, '', '会员专享 in title only'), card('AAAAAAAAAAA', 1), card('EEEEEEEEEEE', 6, 'Members only')];
  const result = readers.members(); assert.equal(result.ready, true); assert.deepEqual(Array.from(result.videos, v => v.id), ['AAAAAAAAAAA', 'BBBBBBBBBBB', 'EEEEEEEEEEE']);
  assert.ok(result.videos.every(v => v.memberOnly)); cards = [card('PPPPPPPPPPP', 1, '')]; assert.equal(readers.members().ready, false);
  ctx.location.href = 'https://www.youtube.com/playlist?list=PLordinary'; assert.equal(readers.members().ready, false);
  console.log('PASS readers: modern cards, independent member badges, public exclusion, deduplication, ordering, exact comments, emoji text, unloaded safety');
}

async function testContent(kind) {
  let now = 0, callback, listener, stopped = false, attempts = 0, pauses = 0, scrolls = 0;
  const task = makeTask('content', kind === 'members' || kind === 'redirect' ? 'members' : 'pinned'); const messages = [];
  const ctx = vm.createContext({ URL, window: { scrollBy() {} }, Date: { now: () => now }, location: { href: kind === 'redirect' ? task.url : task.mode === 'members' ? playlist : task.url, pathname: kind === 'redirect' ? '/@samplechannel' : task.mode === 'members' ? '/playlist' : '/watch' }, setInterval: cb => { callback = cb; return 1; }, clearInterval: () => { stopped = true; },
    document: { querySelector: selector => selector === 'video' ? { pause() { pauses++; } } : kind === 'slow-comments' && now < 12000 ? null : { querySelector: () => kind === 'slow-comments' && scrolls === 0 ? null : {}, scrollIntoView() { scrolls++; } } },
    WatchReaders: { memberList: () => playlist, comments: () => kind === 'offline' || (kind === 'background-delayed' && now < 120000) || (kind === 'slow-comments' && scrolls === 0) ? { ready: false, reason: 'loading' } : { ready: true, comment: { id: task.commentId, text: 'body' } }, members: () => ({ ready: true, videos: [{ id: 'AAAAAAAAAAA', title: '会员', memberOnly: true }], sourceUrl: playlist }) },
    chrome: { runtime: { id: 'content-test', onMessage: { addListener: cb => { listener = cb; } }, sendMessage: async message => { messages.push(clone(message)); if (message.type === 'monitor-config') return kind === 'ordinary' ? { ignored: true } : { task, scanId: 'scan-content' }; if (kind === 'retry' && message.type === 'observation' && attempts++ === 0) throw Error('temporary'); return { ok: true }; } } }
  });
  vm.runInContext(read('content.js'), ctx);
  for (let i = 0; i < 140 && !stopped; i++) {
    now += 1500;
    if (kind === 'background-only' || kind === 'background-delayed') {
      listener({ type: 'monitor-tick', scanId: 'scan-content' }, { id: 'content-test' }, () => {});
      for (let turn = 0; turn < 10; turn++) await Promise.resolve();
    } else await callback();
  }
  assert.equal(stopped, true);
  if (kind === 'ordinary') {
    assert.equal(pauses, 0, 'ordinary YouTube playback is untouched');
    assert.equal(scrolls, 0, 'ordinary YouTube scroll position is untouched');
    assert.equal(messages.length, 1, 'ordinary page stops immediately after ownership check');
  } else if (kind === 'redirect') assert.ok(messages.some(m => m.type === 'member-list' && m.url === playlist));
  else { const reports = messages.filter(m => m.type === 'observation'); assert.equal(reports.length, kind === 'retry' ? 2 : 1); assert.equal(reports.at(-1).payload.ok, kind !== 'offline'); if (kind === 'members') assert.equal(reports[0].payload.videos.length, 1); }
  console.log('PASS content: ' + kind + ', stabilization, bounded scan, bridge retry');
}

function makeAudio() {
  const rate = 22050, seconds = 2, samples = rate * seconds, buffer = Buffer.alloc(44 + samples * 2);
  buffer.write('RIFF'); buffer.writeUInt32LE(buffer.length - 8, 4); buffer.write('WAVEfmt ', 8); buffer.writeUInt32LE(16, 16); buffer.writeUInt16LE(1, 20); buffer.writeUInt16LE(1, 22); buffer.writeUInt32LE(rate, 24); buffer.writeUInt32LE(rate * 2, 28); buffer.writeUInt16LE(2, 32); buffer.writeUInt16LE(16, 34); buffer.write('data', 36); buffer.writeUInt32LE(samples * 2, 40);
  for (let i = 0; i < samples; i++) buffer.writeInt16LE(Math.round(Math.sin(i * 2 * Math.PI * 440 / rate) * 3000), 44 + i * 2);
  const file = path.join(os.tmpdir(), 'qoder-watch-test-tone.wav');
  fs.writeFileSync(file, buffer);
  return file;
}

async function main() {
  const version = fs.readFileSync(path.join(root, 'VERSION'), 'utf8').trim();
  assert.equal(JSON.parse(read('manifest.json')).version, version, 'extension manifest carries the released version');
  assert.ok(fs.readFileSync(path.join(root, 'MonitorCore.cs'), 'utf8').includes('public const string Version = "' + version + '"'), 'desktop program reports the same version');
  JSON.parse(read('manifest.json')); for (const file of ['background.js', 'reader.js', 'content.js', 'inject.js', 'popup.js']) new vm.Script(read(file));
  await testBackground(); testReaders(); for (const kind of ['normal', 'members', 'redirect', 'offline', 'retry', 'ordinary', 'slow-comments', 'background-only', 'background-delayed']) await testContent(kind);
  console.log('PASS all extension tests; synthetic tone at ' + makeAudio());
}

if (process.argv.includes('--serve')) {
  const mock = `const sample={enabled:false,status:'测试环境：未启动',summary:[{name:'博主置顶评论',mode:'pinned',intervalSeconds:120,status:'等待检查'},{name:'会员新视频',mode:'members',intervalSeconds:180,status:'已建立基准，历史视频不提醒'}]};const listeners=[];globalThis.chrome={storage:{local:{get:async()=>({...sample})},onChanged:{addListener:f=>listeners.push(f)}},runtime:{sendMessage:async m=>{if(m.type==='start'){sample.enabled=true;sample.status='测试环境：已恢复现有配对';}if(m.type==='stop'){sample.enabled=false;sample.status='测试环境：全部暂停';}if(m.type==='check'){sample.status=sample.enabled?'测试环境：检查完成':'测试环境：未启动';}listeners.forEach(f=>f());return {ok:true};}}};`;
  const routes = { '/': ['text/html; charset=utf-8', read('popup.html').replace('<script src="popup.js">', '<script src="mock-chrome.js"></script><script src="popup.js">')], '/popup.css': ['text/css; charset=utf-8', read('popup.css')], '/popup.js': ['application/javascript; charset=utf-8', read('popup.js')], '/mock-chrome.js': ['application/javascript; charset=utf-8', mock] };
  http.createServer((req, res) => { const route = routes[req.url]; res.writeHead(route ? 200 : 404, { 'Content-Type': route?.[0] || 'text/plain' }); res.end(route?.[1] || 'Not found'); }).listen(18965, '127.0.0.1', () => console.log('Popup test server ready on 127.0.0.1:18965; mock only, no secrets or email'));
} else main().catch(error => { console.error(error); process.exitCode = 1; });
