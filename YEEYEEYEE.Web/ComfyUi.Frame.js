(() => {
  const labels = location.hostname.split('.');
  const [workerLabel, session] = labels.shift().split('-');
  const worker = Number(workerLabel.slice(1));
  labels.unshift(session);
  const origin = location.protocol + '//' + labels.join('.') + (location.port ? ':' + location.port : '');
  let initialized = false;
  let initializing = false;
  let requestId;
  const originalFetch = window.fetch;
  const allowed = (url, method) => {
    const target = new URL(typeof url === 'string' || url instanceof URL ? url : url.url, location.href);
    return target.origin === location.origin && ['GET', 'HEAD', 'OPTIONS'].includes(method.toUpperCase()) && !/\/(prompt|generate|interrupt|free)(\/|$)/i.test(target.pathname);
  };
  window.fetch = function(input, options) {
    const method = options?.method || input?.method || 'GET';
    if (!allowed(input, method)) return Promise.reject(Error('转换模式禁止写入及外部请求'));
    return originalFetch.call(this, input, options);
  };
  const open = XMLHttpRequest.prototype.open;
  XMLHttpRequest.prototype.open = function(method, url, ...rest) {
    if (!allowed(url, method)) throw Error('转换模式禁止写入及外部请求');
    return open.call(this, method, url, ...rest);
  };
  navigator.sendBeacon = () => false;
  addEventListener('message', async event => {
    if (event.source !== parent || event.origin !== origin || event.data?.session !== session) return;
    if (event.data.kind === 'frontend-init') {
      if (event.data.worker !== worker) return;
      if (initialized) { parent.postMessage({ kind: 'frontend-ready', session, requestId }, origin); return; }
      if (initializing) return;
      initializing = true;
      requestId = event.data.requestId;
      try {
        const started = Date.now();
        while (true) {
          const app = window.comfyAPI?.app?.app;
          const stores = document.getElementById('vue-app')?.__vue_app__?.config.globalProperties.$pinia?._s;
          const spinners = stores ? [...stores.values()].filter(s => 'spinner' in s) : [];
          if (app?.canvas && app.vueAppReady && window.LiteGraph?.registered_node_types?.KSampler && spinners.length && spinners.every(s => !s.spinner)) break;
          if (Date.now() - started > 240000) throw Error('官方前端初始化超时');
          await new Promise(resolve => setTimeout(resolve, 300));
        }
        window.chrome = { webview: { postMessage(payload) {
          const data = JSON.parse(payload);
          if (data.session === session && data.worker === worker) parent.postMessage(data, origin);
        } } };
        (0, eval)(event.data.scripts.guard);
        (0, eval)(event.data.scripts.inspect);
        window.__worker = worker;
        window.__session = session;
        sessionStorage.setItem('library-session', session);
        (0, eval)(event.data.scripts.export);
        initialized = true;
        parent.postMessage({ kind: 'frontend-ready', session, requestId }, origin);
      } catch (error) {
        parent.postMessage({ kind: 'frontend-error', session, requestId, error: error?.message || String(error) }, origin);
      } finally { initializing = false; }
    }
    if (event.data.kind === 'frontend-export' && initialized && event.data.requestId === requestId && event.data.work.Session === session && event.data.work.WorkerId === worker)
      window.__exportLibrary(event.data.work);
  });
})();
