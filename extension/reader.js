(() => {
  const clean = value => (value || '').replace(/\s+/g, ' ').trim();
  const memberBadge = /^(会员专享|會員專享|仅限会员|僅限會員|Members only|Members-only)$/i;

  function readComment(container) {
    const link = container.querySelector('a[href*="&lc="]');
    const body = container.querySelector('#content-text');
    if (!link || !body) return null;
    const id = new URL(link.getAttribute('href'), 'https://www.youtube.com').searchParams.get('lc');
    const copy = body.cloneNode(true);
    for (const image of copy.querySelectorAll('img[alt]')) image.replaceWith(document.createTextNode(image.alt));
    const authorLink = container.querySelector('a#name, a#author-text');
    return id && clean(copy.textContent) ? {
      id, text: clean(copy.textContent), author: clean(authorLink?.textContent),
      authorPath: authorLink?.getAttribute('href') || ''
    } : null;
  }

  function comments(task) {
    const threads = Array.from(document.querySelectorAll('ytd-comments ytd-comment-thread-renderer'));
    const header = document.querySelector('ytd-comments #count');
    const sorting = document.querySelector('ytd-comments #sort-menu');
    if (!header || !sorting) return { ready: false, reason: '评论区尚未加载，请检查页面是否可以查看评论' };
    if (!threads.length) return { ready: false, reason: '评论列表尚未加载，正在等待后台页面加载评论' };
    if (task.mode === 'comment') {
      const containers = Array.from(document.querySelectorAll('ytd-comments ytd-comment-view-model, ytd-comments ytd-comment-renderer'));
      for (const container of containers.length ? containers : threads) {
        const comment = readComment(container);
        if (comment?.id === task.commentId) return { ready: true, comment };
      }
      return { ready: false, reason: '指定评论未找到；保留上次正文，不将未加载或删除当作正文修改' };
    }
    const pinned = threads.find(thread => clean(thread.querySelector('#pinned-comment-badge, ytd-pinned-comment-badge-renderer')?.textContent));
    const comment = pinned ? readComment(pinned) : null;
    return { ready: !pinned || !!comment, comment };
  }

  function memberList() {
    const links = Array.from(document.querySelectorAll('a[href*="list=UUMO"]'));
    for (const link of links) {
      const url = new URL(link.getAttribute('href'), 'https://www.youtube.com');
      if (url.origin === 'https://www.youtube.com' && url.pathname === '/playlist' && /^UUMO[A-Za-z0-9_-]{20,}$/.test(url.searchParams.get('list') || '')) return url.href;
    }
    return null;
  }

  function members() {
    const url = new URL(location.href);
    if (url.pathname !== '/playlist' || !/^UUMO[A-Za-z0-9_-]{20,}$/.test(url.searchParams.get('list') || '')) return { ready: false };
    const cards = Array.from(document.querySelectorAll('yt-lockup-view-model, ytd-playlist-video-renderer'));
    if (!cards.length) return { ready: false };
    const videos = [];
    const seen = new Set();
    for (const card of cards) {
      const badges = Array.from(card.querySelectorAll('yt-badge-view-model, ytd-badge-supported-renderer'));
      if (!badges.some(badge => memberBadge.test(clean(badge.textContent)))) continue;
      const titleLink = card.querySelector('h3 a[href*="watch?"], a#video-title[href*="watch?"]');
      if (!titleLink) continue;
      const videoURL = new URL(titleLink.getAttribute('href'), 'https://www.youtube.com');
      const id = videoURL.searchParams.get('v');
      if (!/^[A-Za-z0-9_-]{11}$/.test(id || '') || seen.has(id)) continue;
      const title = clean(titleLink.textContent);
      if (!title) continue;
      seen.add(id);
      videos.push({ id, title, memberOnly: true, position: Number(videoURL.searchParams.get('index')) || videos.length + 1 });
    }
    if (!videos.length) return { ready: false, reason: '未能确认会员专享视频，请检查账号权限或列表内容' };
    videos.sort((a, b) => a.position - b.position);
    return { ready: true, videos: videos.slice(0, 100), sourceUrl: `https://www.youtube.com/playlist?list=${encodeURIComponent(url.searchParams.get('list'))}` };
  }

  globalThis.WatchReaders = { comments, memberList, members };
})();
