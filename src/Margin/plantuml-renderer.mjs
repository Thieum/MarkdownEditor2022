import { renderToString } from './plantuml/plantuml.js';

window.PLANTUML_STDLIB_BASE = new URL('./plantuml/', location.href).href;
const scripts = new Map();
let resourceError = null;
window.addEventListener('securitypolicyviolation', event => {
    resourceError = 'PlantUML resource blocked by offline policy: ' + event.blockedURI;
});
window.PLANTUML_STDLIB_LOADER = (name, ok, err) => {
    if (!/^(?:[a-z0-9.-]+\.min\.js|themes\.js|emoji\.js|openiconic\.js)$/i.test(name)) {
        resourceError = 'Unsupported PlantUML resource: ' + name;
        err(resourceError);
        return true;
    }
    if (!scripts.has(name)) {
        scripts.set(name, new Promise((resolve, reject) => {
            const script = document.createElement('script');
            script.src = window.PLANTUML_STDLIB_BASE + name;
            script.onload = resolve;
            script.onerror = () => reject(new Error('Could not load bundled PlantUML resource: ' + name));
            document.head.appendChild(script);
        }));
    }
    scripts.get(name).then(ok, error => { resourceError = error.message; err(error.message); });
    return true;
};

function validateSource(source) {
    const lines = source.split(/\r\n|\r|\n/);
    const start = lines.find(line => /^\s*@start\w+\b/.test(line));
    if (!start || !lines.some(line => /^\s*@end\w+\s*$/.test(line))) {
        throw new Error('PlantUML diagrams require explicit @start... and @end... markers.');
    }
    for (const line of lines) {
        if (/^\s*!(?:include(?:_once|_many|sub)?|import)\b/i.test(line) &&
            !/^\s*!include(?:_once|_many)?\s+<[a-z0-9_./-]+>\s*$/i.test(line)) {
            throw new Error('PlantUML only supports bundled standard-library includes; local files and URLs are not supported.');
        }
        if (/^\s*!theme\b.*\bfrom\b/i.test(line) && !/\bfrom\s+<[a-z0-9_./-]+>\s*$/i.test(line)) {
            throw new Error('PlantUML themes must come from the bundled libraries.');
        }
    }
    return lines;
}

window.addEventListener('message', event => {
    if (event.source !== window.parent || !event.data || event.data.type !== 'plantumlRender') return;
    const request = event.data;
    const reply = data => window.parent.postMessage({
        type: 'plantumlResult', id: request.id,
        resources: performance.getEntriesByType('resource').map(entry => entry.name), ...data
    }, '*');
    try {
        const lines = validateSource(request.source);
        resourceError = null;
        renderToString(lines, svg => {
            if (resourceError) reply({ error: resourceError });
            else reply({ svg });
        }, error => reply({ error: String(error) }), { dark: request.dark, maxSvgSize: 8192 });
    } catch (error) {
        reply({ error: error.message || String(error) });
    }
});
window.parent.postMessage({ type: 'plantumlReady' }, '*');
