(() => {
  const c = window.conversion;
  const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
  let frame = null;
  let stopped = false;
  let scripts;
  const finish = (success, error) => {
    if (stopped) return;
    stopped = true;
    document.getElementById('status').textContent = success ? '转换完成，可以返回主窗口。' : (error || '转换失败');
    window.opener?.postMessage({ kind: 'comfyui-conversion', session: c.session, requestId: c.requestId, success, ...(error ? { error } : {}) }, c.mainOrigin);
    frame?.iframe.remove();
  };
  const submit = async data => {
    const response = await fetch('/__conversion/result', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) });
    if (!response.ok) throw Error('转换结果校验失败');
  };
  addEventListener('message', async event => {
    const current = frame;
    if (!current || event.origin !== current.origin || event.source !== current.iframe.contentWindow || event.data?.session !== c.session) return;
    const work = current.work;
    try {
      if (event.data.kind === 'frontend-ready' && event.data.requestId === c.requestId) {
        if (!current.sent) {
          current.sent = true;
          current.iframe.contentWindow.postMessage({ kind: 'frontend-export', session: c.session, requestId: c.requestId, work }, current.origin);
        }
        return;
      }
      let data = event.data;
      if (data.kind === 'frontend-error' && data.requestId === c.requestId)
        data = { kind: 'library-export', session: c.session, requestId: work.RequestId, worker: 0, sourceHash: work.SourceHash, error: data.error };
      if (data.kind !== 'library-export' || data.requestId !== work.RequestId || data.worker !== 0 || data.sourceHash !== work.SourceHash) return;
      if (current.submitting) return;
      current.submitting = true;
      await submit(data);
      current.iframe.remove();
      if (frame === current) frame = null;
    } catch (error) { finish(false, error.message); }
  });
  (async () => {
    try {
      const started = Date.now();
      while (!stopped) {
        if (Date.now() - started > 14 * 60 * 1000) throw Error('转换会话超时');
        if (frame) {
          if (!frame.submitting && Date.now() - frame.started > 270000) {
            frame.submitting = true;
            await submit({ kind: 'library-export', session: c.session, requestId: frame.work.RequestId, worker: 0, sourceHash: frame.work.SourceHash, error: '官方前端页面加载或导出超时' });
            frame.iframe.remove();
            frame = null;
            continue;
          }
          if (!frame.sent) frame.iframe.contentWindow?.postMessage({ kind: 'frontend-init', session: c.session, requestId: c.requestId, worker: 0, scripts }, frame.origin);
          await wait(350);
          continue;
        }
        const response = await fetch('/__conversion/next');
        if (response.status === 204) { await wait(350); continue; }
        if (!response.ok) throw Error('转换会话已失效');
        const work = await response.json();
        if (work.done) { finish(work.success, work.error); break; }
        if (work.WorkerId !== 0) throw Error('无效的转换 worker');
        scripts ??= await (await fetch('/__conversion/scripts')).json();
        const iframe = document.createElement('iframe');
        iframe.style.cssText = 'width:100%;height:70vh';
        const origin = c.frames[0];
        frame = { iframe, origin, work, started: Date.now(), sent: false, submitting: false };
        iframe.src = origin + c.targetPath;
        document.body.append(iframe);
      }
    } catch (error) { finish(false, error.message); }
  })();
})();
