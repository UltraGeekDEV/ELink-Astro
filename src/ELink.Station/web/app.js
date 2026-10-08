// The ELink web interface: a JavaScript leaf of the station's EVent mesh. Like the desktop window it knows only IDs and
// contracts (the field names of the C# types); there is no other API.
import { Leaf, registerLeaf } from './evn.js';

// the "bytes" leaf (a length and then the bytes), used by ProcessedImage.Png
registerLeaf('bytes', r => { r.take(r.i32()); });

const IDS = {
  composeChanged: 'ELink.Compose.Changed', composeSnapshot: 'ELink.Compose.Snapshot',
  scope: id => `ELink.Scope.${id}`,
  imaging: 'ELink.Automation.Imaging',
  frameAdded: 'ELink.Automation.LiveStack.FrameAdded',
  process: 'ELink.Automation.Processing.Process',
  logEntry: 'ELink.Log.Entry', logTail: 'ELink.Log.Tail',
};

const $ = id => document.getElementById(id);
const leaf = new Leaf('web-' + Math.random().toString(36).slice(2, 7), { keepAlive: { interval: 5000, timeout: 20000 } });

// ---------------------------------------------------------------------------------------------------- helpers
const ignore = () => {};
const sleep = ms => new Promise(r => setTimeout(r, ms));

/** Calls a command (Void in, CommandResult out) and says what happened. */
async function command(id, input = null) {
  try {
    const answers = await leaf.call(id, input);
    if (!answers || answers.length === 0) return { Ok: false, Error: 'nobody answered' };
    return answers.find(a => !a.Ok) ?? answers[0];
  } catch (e) { return { Ok: false, Error: String(e.message ?? e) }; }
}

/** The latest value of something that announces its state on an event and answers GetState; retried until the network knows it. */
async function follow(stateId, getId, apply) {
  for (let attempt = 0; attempt < 60; attempt++) {
    try {
      await leaf.hook(stateId, apply);
      const seed = await leaf.call(getId, null);
      if (seed && seed.length) apply(seed[0]);
      return;
    } catch { await sleep(2000); }
  }
}

const phaseClass = p => p === 'Error' ? 'bad' : (p === 'Idle' || p === 'Done' || p === 'OnTarget') ? '' : 'busy';

// ---------------------------------------------------------------------------------------------------- the image
const imageEls = { card: $('image'), label: $('imageLabel'), phase: $('imagePhase'), message: $('imageMessage'), bar: $('imageBar'), depth: $('imageDepth') };
function showImage(s) {
  const active = s.Phase !== 'Idle' || s.TargetSeconds > 0;
  imageEls.card.hidden = !active;
  imageEls.label.textContent = s.Label || 'Image';
  imageEls.phase.textContent = s.Phase;
  imageEls.phase.className = 'chip ' + phaseClass(s.Phase);
  imageEls.message.textContent = s.Message || '';
  const part = s.TargetSeconds > 0 ? Math.min(1, s.MeanSeconds / s.TargetSeconds) : 0;
  imageEls.bar.style.width = (part * 100).toFixed(1) + '%';
  imageEls.depth.textContent = s.TargetSeconds > 0
    ? `${duration(s.MeanSeconds)} of ${duration(s.TargetSeconds)} (the thinnest part: ${duration(s.MinSeconds)})  ·  ${s.Workers?.length ?? 0} scope${(s.Workers?.length ?? 0) === 1 ? '' : 's'} working`
    : '';
}
const duration = s => s >= 3600 ? (s / 3600).toFixed(1) + ' h' : s >= 120 ? Math.round(s / 60) + ' min' : Math.round(s) + ' s';

$('pause').onclick = () => command(`${IDS.imaging}.Pause`);
$('resume').onclick = () => command(`${IDS.imaging}.Resume`);
$('stop').onclick = async () => {
  if (!confirm('Stop this image? What was stacked is kept.')) return;
  const r = await command(`${IDS.imaging}.Abort`);
  if (!r.Ok) alert(r.Error);
};

// ---------------------------------------------------------------------------------------------------- the scopes
const scopeCards = new Map();
function ensureScope(def) {
  const id = def.Id;
  if (scopeCards.has(id)) { scopeCards.get(id).name.textContent = def.DisplayName || id; return; }
  const el = document.createElement('article');
  el.className = 'card scope';
  el.innerHTML = '<div class="row between"><h3></h3><span class="chip">Idle</span></div><p class="message"></p><div class="bar"><div></div></div><p class="hint shots"></p><div class="row"><button class="danger">Stop this scope</button></div>';
  const card = { el, name: el.querySelector('h3'), phase: el.querySelector('.chip'), message: el.querySelector('.message'), bar: el.querySelector('.bar > div'), shots: el.querySelector('.shots') };
  card.name.textContent = def.DisplayName || id;
  el.querySelector('button').onclick = async () => { const r = await command(`${IDS.scope(id)}.Abort`); if (!r.Ok) alert(r.Error); };
  scopeCards.set(id, card);
  $('scopes').append(el);
  $('noScopes').hidden = true;
  follow(`${IDS.scope(id)}.State`, `${IDS.scope(id)}.GetState`, s => {
    card.phase.textContent = s.Phase;
    card.phase.className = 'chip ' + phaseClass(s.Phase);
    card.message.textContent = s.Message || '';
    const planned = s.ShotsPlanned || 0;
    card.bar.style.width = planned > 0 ? Math.min(100, 100 * s.ShotsDone / planned).toFixed(0) + '%' : '0%';
    card.shots.textContent = s.Observing && planned > 0 ? `shot ${s.ShotsDone} of ${planned}` : '';
  });
}
function showComposition(snapshots) {
  const scopes = (snapshots ?? []).flatMap(s => s.Scopes ?? []);
  const ids = new Set(scopes.map(s => s.Id));
  for (const [id, card] of [...scopeCards]) if (!ids.has(id)) { card.el.remove(); scopeCards.delete(id); }
  scopes.forEach(ensureScope);
  $('noScopes').hidden = scopeCards.size > 0;
}

// ---------------------------------------------------------------------------------------------------- the picture
let lastFrame = null, crop = { Left: 0, Top: 0, Width: 1, Height: 1, FlipY: false };
let pictureBusy = false, pictureDirty = false, pictureObjectUrl = null, lastPicture = 0;
const settings = { Stretch: true, BackgroundLevel: 0.25, BlackClipSigmas: 2.8, Linked: true, RemoveGradient: true, GradientDegree: 3, GradientDivide: false, NeutralizeBackground: true, Saturation: 1, GreenReduction: 0 };

const pngOf = bytes => {            // the leaf gives the whole value of a "bytes" field: its length, then the bytes
  const v = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  return bytes.length >= 4 && v.getInt32(0, true) === bytes.length - 4 ? bytes.subarray(4) : bytes;
};

async function refreshPicture() {
  if (pictureBusy) { pictureDirty = true; return; }
  pictureBusy = true;
  try {
    do {
      pictureDirty = false;
      const width = Math.min(1800, Math.max(600, Math.round(innerWidth * (devicePixelRatio || 1))));
      const answers = await leaf.call(IDS.process, { Settings: settings, MaxWidth: width, MaxHeight: width, Source: '', OutOfFocusWeight: 0, PseudoOutput: '' });
      const a = answers?.[0];
      if (!a || !a.Ok) { if (!pictureObjectUrl) $('pictureEmpty').textContent = a?.Message || 'Nothing stacked yet. The picture appears as the first frames land.'; continue; }
      const url = URL.createObjectURL(new Blob([pngOf(a.Png)], { type: 'image/png' }));
      crop = { Left: a.CropLeft, Top: a.CropTop, Width: Math.max(a.CropWidth, 1e-6), Height: Math.max(a.CropHeight, 1e-6), FlipY: a.FlipY };
      const img = $('pictureImg');
      const old = pictureObjectUrl;
      img.hidden = false; $('pictureEmpty').hidden = true;
      img.style.animation = 'none'; void img.offsetWidth; img.style.animation = '';
      img.src = pictureObjectUrl = url;
      if (old) setTimeout(() => URL.revokeObjectURL(old), 3000);
      $('pictureInfo').textContent = `${a.Frames} frame${a.Frames === 1 ? '' : 's'} · ${duration(a.ExposureSeconds)} · ${a.Width}×${a.Height}`;
      lastPicture = Date.now();
    } while (pictureDirty);
  } catch (e) { $('pictureEmpty').textContent = 'could not make the picture: ' + (e.message ?? e); }
  finally { pictureBusy = false; }
}

let pictureTimer = null;
function pictureSoon() {                 // frames can land every few seconds; the picture is made at most that often
  if (pictureTimer) return;
  pictureTimer = setTimeout(() => { pictureTimer = null; refreshPicture(); }, Math.max(1500, 8000 - (Date.now() - lastPicture)));
}

/** A white flash where the newest frame landed, fading out (the grid position of a frame -> its place on the cropped picture). */
function flash(e) {
  const P = (x, y) => { const ny = (y - crop.Top) / crop.Height; return [(x - crop.Left) / crop.Width, crop.FlipY ? 1 - ny : ny]; };
  const pts = [P(e.X0, e.Y0), P(e.X1, e.Y1), P(e.X2, e.Y2), P(e.X3, e.Y3)];
  if (pts.some(p => !Number.isFinite(p[0]) || !Number.isFinite(p[1]))) return;
  const poly = document.createElementNS('http://www.w3.org/2000/svg', 'polygon');
  poly.setAttribute('points', pts.map(p => p.join(',')).join(' '));
  poly.setAttribute('class', 'flash');
  $('landing').append(poly);
  poly.addEventListener('animationend', () => poly.remove());
  setTimeout(() => poly.remove(), 3000);
}

$('picture').onclick = () => $('picture').classList.toggle('full');
addEventListener('keydown', e => { if (e.key === 'Escape') $('picture').classList.remove('full'); });

// ---------------------------------------------------------------------------------------------------- the log
const logEl = $('log');
const pad = (n, w = 2) => String(n).padStart(w, '0');
function logLine(e) {
  const d = e.TimeUtc ? new Date(e.TimeUtc) : new Date();
  const when = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`;
  return `${when} ${String(e.Level).padEnd(5)} ${e.Source || '-'}: ${String(e.Text).replace(/\r?\n/g, ' ⏎ ')}`;
}
function addLog(text, level) {
  const stick = logEl.scrollHeight - logEl.scrollTop - logEl.clientHeight < 40;
  const line = document.createElement('div');
  line.textContent = text;
  if (level) line.className = level;
  logEl.append(line);
  while (logEl.childElementCount > 600) logEl.firstElementChild.remove();
  if (stick) logEl.scrollTop = logEl.scrollHeight;
}
async function loadLog() {
  const tail = (await leaf.call(IDS.logTail, 200))?.[0];
  if (!tail) return;
  $('logFile').textContent = tail.File.split(/[\\/]/).pop();
  logEl.textContent = '';
  for (const text of tail.Text.split('\n').filter(Boolean)) addLog(text, /^\S+ \S+ (warn|error|note)/.exec(text)?.[1]);
  logEl.scrollTop = logEl.scrollHeight;
}
$('noteForm').onsubmit = async ev => {
  ev.preventDefault();
  const text = $('note').value.trim();
  if (!text) return;
  $('note').value = '';
  await leaf.fire(IDS.logEntry, { TimeUtc: new Date().toISOString(), Source: 'you', Level: 'note', Text: text });
};

// ---------------------------------------------------------------------------------------------------- start
leaf.onLink((up, reason) => {
  const chip = $('link');
  chip.dataset.state = up ? 'up' : 'down';
  chip.textContent = up ? 'connected' : 'reconnecting…';
  if (up && started) setTimeout(() => { loadLog().catch(ignore); refreshPicture(); }, 500);
});

let started = false;
async function start() {
  const wsPort = await fetch('/api/wsport').then(r => r.json()).catch(() => 8081);
  const url = `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.hostname}:${wsPort}/`;
  for (;;) { try { await leaf.connect(url); break; } catch { $('link').dataset.state = 'down'; $('link').textContent = 'no station at ' + url; await sleep(3000); } }

  await leaf.hook(IDS.logEntry, e => addLog(logLine(e), ['warn', 'error', 'note'].includes(e.Level) ? e.Level : '')).catch(ignore);
  loadLog().catch(ignore);
  follow(`${IMAGING}.State`, `${IMAGING}.GetState`, showImage);
  for (let i = 0; i < 30; i++) {
    try { await leaf.hook(IDS.composeChanged, showComposition); showComposition(await leaf.call(IDS.composeSnapshot, null)); break; } catch { await sleep(2000); }
  }
  for (let i = 0; i < 30; i++) {
    try { await leaf.hook(IDS.frameAdded, e => { lastFrame = e; flash(e); pictureSoon(); }); break; } catch { await sleep(2000); }
  }
  started = true;
  refreshPicture();
  setInterval(() => { if (Date.now() - lastPicture > 60000 && !document.hidden) refreshPicture(); }, 30000);
}
const IMAGING = IDS.imaging;
start();
