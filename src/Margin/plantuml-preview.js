(function () {
    'use strict';
    if (window.MarkdownPlantUml) return;
    var root = 'http://markdown-editor-host/margin/';
    var origin = new URL(root).origin;
    var frame;
    var ready;
    var active;
    var sequence = 0;
    var queue = Promise.resolve();
    var resources = new Set();

    function reset() {
        if (frame) frame.remove();
        frame = null;
        ready = null;
    }
    window.addEventListener('message', function (event) {
        if (!frame || event.source !== frame.contentWindow || event.origin !== origin || !event.data) return;
        if (event.data.type === 'plantumlReady' && active && active.boot) active.resolve();
        if (event.data.type === 'plantumlResult' && active && event.data.id === active.id) {
            (event.data.resources || []).forEach(function (url) { resources.add(url); });
            if (event.data.error) active.reject(new Error('PlantUML: ' + event.data.error));
            else active.resolve(event.data.svg);
        }
    });
    function timed(operation, description) {
        return new Promise(function (resolve, reject) {
            var timer = setTimeout(function () {
                active = null;
                reset();
                reject(new Error('Timed out ' + description));
            }, 25000);
            active = {
                resolve: function (value) { clearTimeout(timer); active = null; resolve(value); },
                reject: function (error) { clearTimeout(timer); active = null; reject(error); }
            };
            operation(active);
        });
    }
    function boot() {
        if (!ready) {
            ready = timed(function (request) {
                request.boot = true;
                frame = document.createElement('iframe');
                frame.hidden = true;
                frame.setAttribute('sandbox', 'allow-scripts allow-same-origin');
                frame.src = root + 'plantuml-renderer.html';
                frame.addEventListener('error', function () { request.reject(new Error('Could not load the PlantUML renderer.')); });
                document.body.appendChild(frame);
            }, 'loading PlantUML').catch(function (error) { reset(); throw error; });
        }
        return ready;
    }
    function cleanSvg(markup, prefix) {
        var parsed = new DOMParser().parseFromString(markup, 'image/svg+xml');
        var svg = parsed.documentElement;
        if (svg.localName !== 'svg' || parsed.querySelector('parsererror')) throw new Error('PlantUML returned invalid SVG.');
        if (parsed.querySelector('[data-diagram-type="ERROR"]')) throw new Error('PlantUML returned a diagram error.');
        // The published TeaVM engine delivers syntax-error drawings via its success callback.
        var text = Array.from(svg.querySelectorAll('text'));
        if (text.some(function (node) { return /^PlantUML version /.test(node.textContent); }) &&
            text.some(function (node) { return /^\[From .* \(line \d+\)/.test(node.textContent); })) {
            throw new Error('PlantUML: ' + text[text.length - 1].textContent.trim());
        }
        var ids = new Map();
        Array.from(svg.querySelectorAll('[id]')).forEach(function (node) {
            ids.set(node.id, prefix + node.id);
            node.id = prefix + node.id;
        });
        Array.from(svg.querySelectorAll('script, foreignObject')).forEach(function (node) { node.remove(); });
        [svg].concat(Array.from(svg.querySelectorAll('*'))).forEach(function (node) {
            Array.from(node.attributes).forEach(function (attribute) {
                var value = attribute.value;
                if (/^on/i.test(attribute.name) ||
                    (/(?:^|:)href$/i.test(attribute.name) &&
                        (/^\s*javascript:/i.test(value) || (node.localName !== 'a' && !/^(?:#|data:image\/)/i.test(value))))) {
                    node.removeAttribute(attribute.name);
                    return;
                }
                ids.forEach(function (replacement, id) {
                    if (value === '#' + id) value = '#' + replacement;
                    value = value.split('url(#' + id + ')').join('url(#' + replacement + ')');
                });
                attribute.value = value;
            });
        });
        return new XMLSerializer().serializeToString(svg);
    }
    function renderSource(source, theme) {
        var result = queue.catch(function () {}).then(async function () {
            await boot();
            var id = ++sequence;
            var markup = await timed(function (request) {
                request.id = id;
                frame.contentWindow.postMessage({ type: 'plantumlRender', id: id, source: source, dark: theme === 'dark' }, origin);
            }, 'rendering PlantUML');
            return cleanSvg(markup, 'plantuml-' + id + '-');
        });
        // Recover scheduling after a reported failure; the caller still receives the rejection.
        queue = result;
        return result;
    }
    window.MarkdownPlantUml = {
        resourceUrls: resources,
        renderSource: renderSource,
        render: async function (nodes, theme) {
            for (var node of nodes) node.innerHTML = await renderSource(node.textContent, theme);
        }
    };
})();
