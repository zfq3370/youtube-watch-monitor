const statusBox = document.getElementById('status');
const taskBox = document.getElementById('tasks');
const modes = { pinned: '博主置顶评论', comment: '指定评论变更', members: '会员专属新视频' };
async function refresh() {
  const data = await chrome.storage.local.get(['enabled', 'status', 'checkedAt', 'summary']);
  statusBox.textContent = `${data.enabled ? '助手运行中' : '助手未开启'}\n${data.status || '请先启动新版桌面程序，再配对'}${data.checkedAt ? '\n最近报告：' + new Date(data.checkedAt).toLocaleString() : ''}`;
  taskBox.replaceChildren();
  for (const task of data.summary || []) {
    const card = document.createElement('section');
    const title = document.createElement('strong');
    title.textContent = task.name;
    const detail = document.createElement('p');
    detail.textContent = `${modes[task.mode] || task.mode} · 每 ${task.intervalSeconds} 秒\n${task.status || '等待检查'}${task.checkedAt ? '\n' + new Date(task.checkedAt).toLocaleString() : ''}`;
    card.append(title, detail);
    taskBox.append(card);
  }
}
for (const type of ['start', 'stop', 'check']) {
  document.getElementById(type).addEventListener('click', async () => {
    statusBox.textContent = '处理中…';
    const codeInput = document.getElementById('code');
    let pairCode = codeInput.value.trim();
    if (type === 'start' && !pairCode) pairCode = (await chrome.storage.local.get('pairCode')).pairCode || '';
    try {
      const result = await chrome.runtime.sendMessage({ type, pairCode });
      codeInput.value = '';
      if (result?.error) statusBox.textContent = result.error;
      else await refresh();
    } catch (error) { statusBox.textContent = error.message; }
  });
}
chrome.storage.onChanged.addListener(refresh);
refresh();
