(() => {
    const sleep = ms => new Promise(r => setTimeout(r, ms));
    const graphs = app => {
        const out = [], seen = new Set();
        function walk(g, path) {
            if (!g || seen.has(g)) return;
            seen.add(g); out.push({ g, path });
            for (const n of g._nodes || []) { walk(n.subgraph, path + '/' + n.id); walk(n.subGraph, path + '/' + n.id); }
            for (const [id, child] of (g.subgraphs instanceof Map ? g.subgraphs : [])) walk(child, path + '/definition/' + id);
        }
        walk(app.rootGraph || app.graph, 'root'); return out;
    };
    window.__dynamicSettle = async app => {
        for (const { g } of graphs(app)) for (const n of g._nodes || []) if (n.type === 'LoadVideo') {
            const w = n.widgets?.find(w => w.name === 'file'); w?.callback?.(w.value);
            const end = performance.now() + 2200;
            while (!n.widgets?.some(w => w.name === 'video-preview') && performance.now() < end) { app.canvas.draw(true, true); await sleep(50); }
        }
    };
    let defs;
    window.__dynamicInspect = async (app, raw) => {
        defs ??= await (await fetch(new URL('object_info', document.baseURI))).json();
        const errors = [], resources = [];
        for (const { g, path } of graphs(app)) for (const n of g._nodes || []) {
            if (n.has_errors || n.constructor.name === 'LGraphNode') errors.push({ path, id: n.id, type: n.type });
            const spec = { ...defs[n.type]?.input?.required, ...defs[n.type]?.input?.optional };
            for (const w of n.widgets || []) {
                const options = spec[w.name]?.[1];
                if (!(options?.video_upload || options?.audio_upload || options?.image_upload || options?.animated_image_upload) || typeof w.value !== 'string' || !w.value.trim()) continue;
                let value = w.value, type = 'input';
                const match = value.match(/\s*\[(input|output|temp)\]$/);
                if (match) { type = match[1]; value = value.slice(0, match.index); }
                value = value.replaceAll('\\', '/');
                const slash = value.lastIndexOf('/');
                const url = new URL('view?' + new URLSearchParams({ filename: value.slice(slash + 1), subfolder: slash >= 0 ? value.slice(0, slash) : '', type }), document.baseURI);
                try {
                    const response = await fetch(url, { cache: 'no-store', headers: { Range: 'bytes=0-1023' } });
                    resources.push({ path, nodeId: n.id, widget: w.name, value: w.value, status: response.status, ok: response.ok });
                    await response.body?.cancel();
                } catch (e) { resources.push({ path, nodeId: n.id, widget: w.name, value: w.value, error: String(e), ok: false }); }
            }
        }
        return { errors, resources, expectedSubgraphs: raw.definitions?.subgraphs || [] };
    };
    return 'inspect-ready';
})()
