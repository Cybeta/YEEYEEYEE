(() => {
    window.__blocked = window.__blocked || [];
    if (!window.__libraryGuard) {
        const isGeneration = u => /\/(prompt|generate)\/?$/i.test(new URL(u, location.href).pathname);
        const allowed = (method, url) => ['GET', 'HEAD', 'OPTIONS'].includes(method.toUpperCase()) && !isGeneration(url);
        const fetchOriginal = window.fetch;
        window.fetch = function (input, options) {
            const url = typeof input === 'string' ? input : (input.url || String(input));
            if (!allowed(options?.method || input?.method || 'GET', url)) {
                window.__blocked.push(url);
                return Promise.reject(Error('generation blocked'));
            }
            return fetchOriginal.call(this, input, options);
        };
        const open = XMLHttpRequest.prototype.open;
        XMLHttpRequest.prototype.open = function (method, url, ...args) {
            if (!allowed(method, url)) { window.__blocked.push(String(url)); throw Error('generation blocked'); }
            return open.call(this, method, url, ...args);
        };
        navigator.sendBeacon = url => { window.__blocked.push(String(url)); return false; };
        window.__libraryGuard = true;
    }
    window.comfyAPI.app.app.queuePrompt = async () => { throw Error('generation blocked'); };
    return 'guard-ready';
})()
