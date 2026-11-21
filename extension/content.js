(() => {
  let task, scanId, finished = false, busy = false, lastKey = '', steady = 0, reason = '', scrollPass = 0;
  const started = Date.now();
  const timer = setInterval(scan, 1500);
  chrome.runtime.onMessage.addListener((message, sender, respond) => {
    if (sender.id !== chrome.runtime.id || message.type !== 'monitor-tick') return;
    if (scanId && message.scanId !== scanId) { respond({ ignored: true }); return; }
    scan();
    respond({ ok: true });
  });
  async function scan() {
    if (finished || busy) return;
    busy = true;
    try {
      if (!task) {
        const config = await chrome.runtime.sendMessage({ type: 'monitor-config' });
        if (config?.ignored) { stop(); return; }
        if (config?.task) { task = config.task; scanId = config.scanId; }
      }
      if (!task) { if (Date.now() - started > 150000) stop(); return; }
      document.querySelector('video')?.pause();
      if (Date.now() - started > 150000) {
        await finish({ ok: false, error: reason || '页面未能稳定读取，请检查网络、登录、会员资格或目标链接' });
        return;
      }
      if (task.mode === 'members' && location.pathname !== '/playlist') {
        const list = WatchReaders.memberList();
        if (list) {
          const result = await chrome.runtime.sendMessage({ type: 'member-list', url: list, scanId });
          if (result?.ok) stop();
        } else reason = '频道首页未发现会员专享列表；可将该频道的会员专享列表链接直接填入任务';
        return;
      }
      if (task.mode !== 'members') {
        const comments = document.querySelector('ytd-comments#comments');
        if (comments && !comments.querySelector('ytd-comment-thread-renderer')) {
          comments.scrollIntoView({ block: 'start', behavior: 'instant' });
          if (++scrollPass % 2 === 0) window.scrollBy(0, -80);
        }
      }
      const result = task.mode === 'members' ? WatchReaders.members() : WatchReaders.comments(task);
      if (!result.ready) { reason = result.reason || ''; lastKey = ''; steady = 0; return; }
      const payload = task.mode === 'members' ? { videos: result.videos, sourceUrl: result.sourceUrl } : { comment: result.comment };
      const key = JSON.stringify(payload);
      steady = key === lastKey ? steady + 1 : 0;
      lastKey = key;
      if (steady >= 3 && Date.now() - started >= 15000) await finish({ ok: true, ...payload });
    } catch (error) {
      reason = error.message;
    } finally { busy = false; }
  }

  function stop() { finished = true; clearInterval(timer); }
  async function finish(payload) {
    try {
      const result = await chrome.runtime.sendMessage({ type: 'observation', scanId, payload });
      if (result?.ok || result?.ignored) stop();
      else reason = result?.error || '本机程序未接收报告';
    } catch (error) { reason = error.message; }
  }
})();
