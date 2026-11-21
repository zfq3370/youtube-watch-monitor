(() => {
  if (window.__qoderReaderPatch) return;
  if (!(document.hidden || document.visibilityState === 'hidden')) return;
  window.__qoderReaderPatch = true;
  for (const [name, value] of [['visibilityState', 'visible'], ['hidden', false]]) {
    try { Object.defineProperty(Document.prototype, name, { configurable: true, get: () => value }); } catch { /* Frozen in some builds; intersection patch still helps. */ }
  }
  const RealObserver = window.IntersectionObserver;
  window.IntersectionObserver = class extends RealObserver {
    constructor(callback, options) { super(callback, options); this.__callback = callback; }
    observe(target, options) {
      super.observe(target, options);
      if (!target || typeof target.getBoundingClientRect !== 'function') return;
      setTimeout(() => {
        try {
          const rect = target.getBoundingClientRect();
          this.__callback([{ target, isIntersecting: true, intersectionRatio: 1, boundingClientRect: rect, intersectionRect: rect, scrollRootBoundingRect: rect, rootBounds: null, rootBoundingClientData: null, time: performance.now() }], this);
        } catch { /* Element detached before the deferred callback ran. */ }
      }, 50);
    }
  };
  const realFrame = window.requestAnimationFrame.bind(window);
  window.requestAnimationFrame = callback => {
    let handled = false;
    const id = realFrame(value => { handled = true; callback(value); });
    setTimeout(() => { if (!handled) { handled = true; callback(performance.now()); } }, 64);
    return id;
  };
})();
