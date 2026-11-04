// Headless Firefox over WebDriver BiDi: open a page, run steps (JS in the page, screenshots), print console output.
//   node ff.mjs URL steps.mjs OUTDIR [WIDTHxHEIGHT]
import { spawn } from 'node:child_process';
import { mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { homedir } from 'node:os';

const [,, url, stepsFile, out = '.', size = '1200x1400'] = process.argv;
const [W, H] = size.split('x').map(Number);
const profile = `${homedir()}/snap/firefox/common/elink-ff-test`;   // the snap Firefox can only read profiles here
rmSync(profile, { recursive: true, force: true }); mkdirSync(profile, { recursive: true });
writeFileSync(`${profile}/user.js`, 'user_pref("geo.enabled", false);\nuser_pref("browser.shell.checkDefaultBrowser", false);\nuser_pref("datareporting.policy.dataSubmissionEnabled", false);\n');
const port = 9300 + Math.floor(Math.random() * 500);
const ff = spawn('firefox', ['--headless', '--no-remote', '--profile', profile, `--remote-debugging-port=${port}`, `--width=${W}`, `--height=${H}`], { stdio: ["ignore", "ignore", "pipe"], detached: true });
ff.stderr.on('data', () => {});
const t0 = Date.now();
const log = (...a) => console.log(((Date.now() - t0) / 1000).toFixed(1).padStart(6), ...a);

let ws;
for (let i = 0; i < 60; i++) {
  try { ws = new WebSocket(`ws://127.0.0.1:${port}/session`); await new Promise((ok, bad) => { ws.onopen = ok; ws.onerror = bad; }); break; }
  catch { ws = null; await new Promise(r => setTimeout(r, 500)); }
}
if (!ws) { console.error("no firefox"); try { process.kill(-ff.pid); } catch { } process.exit(1); }
let n = 0; const waiting = new Map();
ws.onmessage = m => {
  const msg = JSON.parse(m.data);
  if (msg.id && waiting.has(msg.id)) { const { ok, bad } = waiting.get(msg.id); waiting.delete(msg.id); msg.type === 'error' ? bad(new Error(msg.error + ': ' + msg.message)) : ok(msg.result); }
  else if (msg.method === 'log.entryAdded') { const e = msg.params; log(`[${e.level}]`, e.text ?? e.args?.map(a => a.value).join(' ')); }
};
const send = (method, params = {}) => new Promise((ok, bad0) => { const id = ++n; const bad = e => bad0(new Error(method + ': ' + e.message)); waiting.set(id, { ok, bad }); ws.send(JSON.stringify({ id, method, params })); });
await send('session.new', { capabilities: {} });
await send('session.subscribe', { events: ['log.entryAdded'] });
const { context } = await send('browsingContext.create', { type: 'tab' });
await send('browsingContext.setViewport', { context, viewport: { width: W, height: H } });

const page = {
  log,
  async go(u) { await send('browsingContext.navigate', { context, url: u, wait: 'complete' }); },
  async js(expr) {
    const r = await send('script.evaluate', { expression: `(async () => { ${expr} })()`, target: { context }, awaitPromise: true, resultOwnership: 'none' });
    if (r.type === 'exception') throw new Error(r.exceptionDetails.text);
    return unwrap(r.result);
  },
  async shot(name, full = true) {
    const r = await send('browsingContext.captureScreenshot', { context, origin: full ? 'document' : 'viewport' });
    writeFileSync(`${out}/${name}.png`, Buffer.from(r.data, 'base64')); log('screenshot', name);
  },
  async viewport(w, h) { await send('browsingContext.setViewport', { context, viewport: { width: w, height: h } }); },
  sleep: ms => new Promise(r => setTimeout(r, ms)),
  async until(expr, ms = 20000) { const end = Date.now() + ms; for (;;) { if (await page.js(`return !!(${expr})`)) return true; if (Date.now() > end) return false; await page.sleep(300); } },
};
function unwrap(v) {
  if (!v) return v;
  switch (v.type) {
    case 'undefined': return undefined; case 'null': return null;
    case 'string': case 'number': case 'boolean': return v.value;
    case 'array': return v.value.map(unwrap);
    case 'object': return Object.fromEntries(v.value.map(([k, x]) => [typeof k === 'string' ? k : unwrap(k), unwrap(x)]));
    default: return v.type;
  }
}
try {
  await page.go(url);
  const steps = (await import(new URL(stepsFile, `file://${process.cwd()}/`).href)).default;
  await steps(page);
} catch (e) { log('FAILED', e.message); process.exitCode = 1; }
finally { try { await Promise.race([send('browser.close'), new Promise(r => setTimeout(r, 3000))]); } catch { } try { process.kill(-ff.pid, "SIGTERM"); } catch { } setTimeout(() => process.exit(), 500); }
