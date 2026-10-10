window.__exportLibrary = request => {
    const { RequestId: requestId, WorkerId: worker, Session: session, Raw: sourceText, SourceHash: expectedHash } = request;
    window.__activeLibraryId = requestId;
    const cancelled = () => window.__activeLibraryId !== requestId;
    const sleep = ms => new Promise(r => setTimeout(r, ms));
    const trace = [];
    const send = result => {
        if (cancelled()) return;
        const payload = JSON.stringify({ kind: 'library-export', requestId, worker, session, sourceHash: expectedHash, ...result });
        if (window.chrome?.webview) window.chrome.webview.postMessage(payload);
        else if (window.webkit?.messageHandlers?.avalonia) window.webkit.messageHandlers.avalonia.postMessage(payload);
        else throw Error('NativeWebView 消息桥不可用');
    };
    (async () => {
        let restoreLoad;
        try {
            if (!window.__libraryGuard) throw Error('前端写请求保护未安装');
            if (window.__worker !== worker || window.__session !== session || sessionStorage.getItem('library-session') !== session) throw Error('worker/session 身份不一致');
            if (!crypto.subtle) throw Error('当前来源不支持 SHA-256 校验，请使用 HTTPS 或 localhost');
            const sourceHash = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(sourceText)))).map(b => b.toString(16).padStart(2, '0')).join('');
            if (sourceHash !== expectedHash) throw Error('原稿 sourceHash 不一致');
            const raw = JSON.parse(sourceText);
            const { app } = await import(new URL('scripts/app.js', document.baseURI).href);
            app.queuePrompt = async () => { throw Error('generation blocked'); };
            const snapshot = g => (g?._nodes || []).map(n => ({ id: String(n.id), type: n.type }));
            const record = stage => trace.push({ stage, time: Date.now(), current: snapshot(app.graph), root: snapshot(app.rootGraph), currentIsRoot: app.graph === app.rootGraph });
            const originalLoad = app.loadGraphData;
            app.loadGraphData = async function (...args) {
                trace.push({ stage: 'loadGraphData', name: args[3], nodes: (args[0]?.nodes || []).map(n => ({ id: String(n.id), type: n.type })), stack: new Error().stack });
                return await originalLoad.apply(this, args);
            };
            restoreLoad = () => { app.loadGraphData = originalLoad; };
            const setup = String(app.setup), register = String(app.registerNodes);
            const complete = () => !!app.positionConversion && /await this\.registerNodes\(\)/.test(setup)
                && setup.indexOf('this.positionConversion=') > setup.indexOf('invokeExtensionsAsync(`setup`)')
                && register.includes('invokeExtensionsAsync(`registerCustomNodes`)');
            const definitions = raw.definitions?.subgraphs || [], containers = new Set(definitions.map(g => String(g.id)));
            const types = [...new Set([raw, ...definitions].flatMap(g => (g.nodes || []).map(n => n.type)))].filter(t => t && !containers.has(t));
            const end = Date.now() + 15000;
            while (types.some(t => !window.LiteGraph?.registered_node_types?.[t]) && !complete() && Date.now() < end) { if (cancelled()) return; await sleep(250); }
            const missing = types.filter(t => !window.LiteGraph?.registered_node_types?.[t]);
            if (missing.length) throw Error('未注册节点：' + missing.join(','));
            const matches = () => app.graph._nodes.length === raw.nodes.length && raw.nodes.every(n => app.graph._nodes.some(a => String(a.id) === String(n.id) && a.type === n.type));
            for (let attempt = 0; attempt < 3; attempt++) {
                if (cancelled()) return;
                app.graph.clear();
                await app.loadGraphData(structuredClone(raw), true, false, 'library-export', { skipAssetScans: false });
                const until = Date.now() + 5000;
                while (!matches() && Date.now() < until) { if (cancelled()) return; await sleep(100); }
                if (matches()) break;
            }
            record('loaded');
            await window.__dynamicSettle(app);
            record('settled');
            if (!matches()) throw Error('原稿 id/type 身份不一致');
            const inspection = await window.__dynamicInspect(app, raw);
            record('inspected');
            if (inspection.errors.length) throw Error('坏节点：' + inspection.errors.map(n => n.type).join(','));
            const unavailable = inspection.resources.filter(r => !r.ok);
            if (unavailable.length) throw Error('工作流资源不可用：' + unavailable.map(r => r.value).join(','));
            let previous = '';
            for (let i = 0; i < 10; i++) {
                if (cancelled()) return;
                record('before-export-' + i);
                if (!matches()) throw Error('导出前身份改变');
                const result = await app.graphToPrompt();
                record('after-export-' + i);
                if (!matches()) throw Error('导出后身份改变');
                if (!result.output || !Object.keys(result.output).length) throw Error('官方导出为空');
                const current = JSON.stringify(result.output);
                if (current === previous) { send({ api: result.output, sourceHash, identityVerified: true, fullOfficialLoad: true, inspection, trace }); return; }
                previous = current; await sleep(100);
            }
            throw Error('官方导出未稳定');
        } catch (e) { send({ error: (e.message || String(e)) + '\n诊断:' + JSON.stringify(trace) }); }
        finally { restoreLoad?.(); }
    })();
};
'export-ready'
