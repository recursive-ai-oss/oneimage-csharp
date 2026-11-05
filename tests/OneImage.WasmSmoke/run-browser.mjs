// Runs the browser-wasm smoke app in headless Chrome and exits nonzero if it fails.
//
//   node run-browser.mjs <framework-root> [--site <dir>] [--chrome <path>]
//
// <framework-root> is the directory that holds _framework/ (the wwwroot of a build or publish output);
// the page (index.html, main.js) comes from ./wwwroot unless --site says otherwise.
// Needs only Node 22 (built-in WebSocket and fetch) and a Chrome or Chromium binary. No npm packages.
import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
let frameworkRoot = null;
let site = path.join(here, 'wwwroot');
let chrome = process.env.CHROME_BIN || 'google-chrome';
for (let i = 0; i < args.length; i++) {
    if (args[i] === '--site') site = args[++i];
    else if (args[i] === '--chrome') chrome = args[++i];
    else frameworkRoot = args[i];
}
if (!frameworkRoot) {
    console.error('usage: node run-browser.mjs <framework-root> [--site <dir>] [--chrome <path>]');
    process.exit(2);
}
const timeoutMs = Number(process.env.SMOKE_TIMEOUT_MS || 240000);

// Anything the Mono runtime prints when it hits an internal failure.
const badConsole = /\[MONO\]|Assertion|should not be reached|memory access out of bounds|RuntimeError|unreachable|Aborted\(/i;

const types = {
    '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.json': 'application/json',
    '.wasm': 'application/wasm', '.map': 'application/json',
};
const server = http.createServer((req, res) => {
    const rel = decodeURIComponent(new URL(req.url, 'http://x').pathname);
    const name = rel === '/' ? '/index.html' : rel;
    for (const root of [site, frameworkRoot]) {
        const file = path.join(root, path.normalize(name));
        if (file.startsWith(root) && fs.existsSync(file) && fs.statSync(file).isFile()) {
            res.writeHead(200, {
                'Content-Type': types[path.extname(file)] || 'application/octet-stream',
                // Cross-origin isolation, so a multithreaded runtime build can run too.
                'Cross-Origin-Opener-Policy': 'same-origin', 'Cross-Origin-Embedder-Policy': 'require-corp',
            });
            fs.createReadStream(file).pipe(res);
            return;
        }
    }
    res.writeHead(404);
    res.end();
});
await new Promise(r => server.listen(0, '127.0.0.1', r));
const port = server.address().port;

const profile = fs.mkdtempSync(path.join(process.env.TMPDIR || os.tmpdir(), 'smoke-chrome-'));
const proc = spawn(chrome, [
    '--headless=new', '--no-sandbox', '--disable-gpu', '--no-first-run', '--remote-debugging-port=0',
    `--user-data-dir=${profile}`, 'about:blank',
], { stdio: ['ignore', 'ignore', 'pipe'] });

let failure = null;
const lines = [];
async function cleanup() {
    const exited = new Promise(r => proc.once('exit', r));
    proc.kill('SIGKILL');
    await exited;
    server.close();
    try { fs.rmSync(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 }); } catch { }
}
async function finish(code) {
    for (const l of lines) console.log(l);
    if (failure) console.error('FAIL: ' + failure);
    console.log(code === 0 ? 'browser smoke passed' : 'browser smoke FAILED');
    await cleanup();
    process.exit(code);
}
const timer = setTimeout(() => { failure ??= 'timed out'; finish(1); }, timeoutMs);

const wsUrl = await new Promise((resolve, reject) => {
    let buf = '';
    proc.stderr.on('data', d => {
        buf += d;
        const m = /DevTools listening on (ws:\/\/\S+)/.exec(buf);
        if (m) resolve(m[1]);
    });
    proc.once('exit', () => reject(new Error('Chrome exited early: ' + buf)));
});

const browser = new WebSocket(wsUrl);
await new Promise(r => browser.addEventListener('open', r));
let nextId = 1;
const pending = new Map();
const handlers = [];
browser.addEventListener('message', ev => {
    const msg = JSON.parse(ev.data);
    if (msg.id && pending.has(msg.id)) { pending.get(msg.id)(msg); pending.delete(msg.id); }
    else handlers.forEach(h => h(msg));
});
function send(method, params = {}, sessionId) {
    const id = nextId++;
    return new Promise(resolve => {
        pending.set(id, resolve);
        browser.send(JSON.stringify({ id, method, params, sessionId }));
    });
}

const { result: { targetId } } = await send('Target.createTarget', { url: 'about:blank' });
const { result: { sessionId } } = await send('Target.attachToTarget', { targetId, flatten: true });
handlers.push(msg => {
    if (msg.sessionId !== sessionId) return;
    if (msg.method === 'Runtime.consoleAPICalled') {
        const text = msg.params.args.map(a => a.value ?? a.description ?? '').join(' ');
        lines.push(`[${msg.params.type}] ${text}`);
        if (badConsole.test(text)) failure ??= 'console: ' + text.split('\n')[0];
    } else if (msg.method === 'Runtime.exceptionThrown') {
        const d = msg.params.exceptionDetails;
        const text = d.exception?.description ?? d.text;
        lines.push('[exception] ' + text);
        failure ??= 'uncaught exception: ' + text.split('\n')[0];
    }
});
await send('Runtime.enable', {}, sessionId);
await send('Page.enable', {}, sessionId);
await send('Page.navigate', { url: `http://127.0.0.1:${port}/index.html` }, sessionId);

while (true) {
    await new Promise(r => setTimeout(r, 250));
    const r = await send('Runtime.evaluate', { expression: 'document.title' }, sessionId);
    const title = r.result?.result?.value ?? '';
    if (failure) break;
    if (title.startsWith('smoke:')) {
        if (title !== 'smoke:pass') failure = title;
        break;
    }
}
clearTimeout(timer);
finish(failure ? 1 : 0);
