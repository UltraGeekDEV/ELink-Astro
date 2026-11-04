// The ELink web interface: a JavaScript leaf of the station's EVent mesh. Like the desktop window it knows only IDs and
// contracts (the field names of the C# types); there is no other API. Everything a night needs is here: what the image is
// doing, starting a new one, the kept ones, the queue, the rig (devices, mount, scope, site) and the session log.
import { Leaf, registerLeaf } from './evn.js';

// the "bytes" leaf (a length and then the bytes): ProcessedImage.Png, ImagingState.Map
registerLeaf('bytes', r => { r.take(r.i32()); });

const IDS = {
  composeChanged: 'ELink.Compose.Changed', composeSnapshot: 'ELink.Compose.Snapshot',
  defineScope: 'ELink.Compose.Define', defineTrain: 'ELink.Compose.DefineTrain', definePointer: 'ELink.Compose.DefineMountPointer',
  scope: id => `ELink.Scope.${id}`, train: id => `ELink.Train.${id}`, pointer: id => `ELink.Pointer.${id}`,
  device: (kind, id) => `ELink.${kind}.${id}`, devices: 'ELink.Devices.List', devicesAnnounce: 'ELink.Devices.Announce',
  imaging: 'ELink.Automation.Imaging', scheduler: 'ELink.Automation.Scheduler',
  frameAdded: 'ELink.Automation.LiveStack.FrameAdded', process: 'ELink.Automation.Processing.Process',
  site: 'ELink.Site.home', backlash: 'ELink.Automation.Backlash', profiles: 'ELink.Profiles', storage: 'ELink.Automation.Storage', solvers: 'ELink.Automation.Solve.Solvers',
  logEntry: 'ELink.Log.Entry', logTail: 'ELink.Log.Tail',
};

const $ = id => document.getElementById(id);
const leaf = new Leaf('web-' + Math.random().toString(36).slice(2, 7), { keepAlive: { interval: 5000, timeout: 20000 } });

// ---------------------------------------------------------------------------------------------------- the contracts' defaults
// The leaf sends every field of a record, so what the page sends starts from the same defaults as the C# types.
const NaN_ = Number.NaN;
const D = {
  SkyTarget: () => ({ RaHours: 0, DecDegrees: 0, Epoch: 'J2000' }),
  ShooterExposure: () => ({ Seconds: 1, FrameType: 'Light', Filter: '', BinX: 0, BinY: 0, Gain: NaN_, Iso: '', Offset: NaN_ }),
  ImagingRequest: () => ({
    Label: 'Image', Center: D.SkyTarget(), WidthDegrees: 0, HeightDegrees: 0, PositionAngleDegrees: 0, ScopeIds: [], Exposure: D.ShooterExposure(),
    TargetSeconds: 0, StepoverDegrees: 0, DitherArcsec: 30, MaxVisits: 0, WeatherId: '', LiveStack: true, OutputPixelScaleArcsec: 0,
    SlewTimeoutSeconds: 300, SkyWaitSeconds: 120, Resume: true, Layers: [],
  }),
  ScheduleEntry: () => ({ Request: D.ImagingRequest(), Priority: 0, Enabled: true, MinAltitudeDegrees: NaN_, RequireDark: true, MinMoonSeparationDegrees: 0, MaxMoonIllumination: 1, NotBeforeUtc: '', NotAfterUtc: '' }),
  SiteConfig: () => ({ Label: 'Home', LatitudeDegrees: NaN_, LongitudeDegrees: NaN_, ElevationMeters: 0, GpsId: '', PushToMounts: true, MinAltitudeDegrees: 0, Horizon: [] }),
  TrainCamera: () => ({ CameraId: '', Role: 'Imaging', PixelSizeUm: 0, SensorWidth: 0, SensorHeight: 0, Gain: NaN_, Offset: NaN_, CoolTo: NaN_, CoolDegreesPerMinute: 3, WarmTo: 10, PseudoMono: false }),
  ImagingTrainDefinition: () => ({ Id: '', Label: '', FocalLengthMm: 0, ApertureMm: 0, Cameras: [], FilterWheelId: '', FocuserId: '', RotatorId: '', FocusOffsets: [] }),
  ScopeDefinition: () => ({
    Id: '', DisplayName: '', Pointers: [], Shooters: [], GuiderId: '', GuideShooterId: '', GuideOutput: 'Pulse', GuidePortId: '', GuideTargetId: '', GuideExposureSeconds: 2,
    CenterAfterSlew: false, CenterShooterId: '', CenterToleranceArcmin: 1, CenterExposureSeconds: 3, CenterMaxTries: 4, GradeFrames: true, FocusOnStart: false,
    RefocusEveryMinutes: 0, RefocusTemperatureDelta: 0, RefocusOnFilterChange: false, RefocusHfrIncreasePercent: 0, FocusExposureSeconds: 3, FocusStepSize: 3000, FocusSamples: 7,
    MeridianFlip: true, FlipAfterHours: 0.1, SiteId: 'home', DitherEvery: 0, DitherPixels: 5, SettlePixels: 1.5, SettleSeconds: 10, SettleTimeoutSeconds: 120,
  }),
  ScopeShooterRef: () => ({ Id: '', OffsetEastArcmin: 0, OffsetNorthArcmin: 0 }),
  ProcessSettings: () => ({ Stretch: true, BackgroundLevel: 0.25, BlackClipSigmas: 2.8, Linked: true, RemoveGradient: true, GradientDegree: 3, GradientDivide: false, NeutralizeBackground: true, Saturation: 1, GreenReduction: 0 }),
};
/** A value of a contract type: its defaults, with what is given on top (records inside are completed too). */
function make(type, given = {}) { return merge(D[type](), given); }
function merge(base, given) {
  for (const [k, v] of Object.entries(given ?? {})) {
    if (v === undefined) continue;
    base[k] = v && typeof v === 'object' && !Array.isArray(v) && !(v instanceof Uint8Array) && base[k] && typeof base[k] === 'object' && !Array.isArray(base[k]) ? merge(base[k], v) : v;
  }
  return base;
}
const clone = v => structuredClone(v);

// ---------------------------------------------------------------------------------------------------- helpers
const ignore = () => {};
const sleep = ms => new Promise(r => setTimeout(r, ms));
const fin = Number.isFinite;
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const blob = bytes => {                 // the leaf gives the whole value of a "bytes" field: its length, then the bytes
  if (!bytes || bytes.length < 4) return new Uint8Array(0);
  const v = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  return v.getInt32(0, true) === bytes.length - 4 ? bytes.subarray(4) : bytes;
};
const duration = s => !fin(s) ? '—' : s >= 5400 ? (s / 3600).toFixed(1) + ' h' : s >= 120 ? Math.round(s / 60) + ' min' : Math.round(s) + ' s';
const clock = d => d ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : '—';
const utc = s => { if (!s) return null; const d = new Date(s); return isNaN(d) ? null : d; };
const hms = (v, hours) => {
  if (!fin(v)) return '—';
  const sign = v < 0 ? '-' : hours ? '' : '+'; v = Math.abs(v);
  const a = Math.floor(v), b = Math.floor((v - a) * 60), c = Math.round(((v - a) * 60 - b) * 60);
  const [B, C] = c === 60 ? [b + 1, 0] : [b, c];
  return hours ? `${sign}${String(a).padStart(2, '0')}h${String(B).padStart(2, '0')}m${String(C).padStart(2, '0')}s` : `${sign}${a}°${String(B).padStart(2, '0')}′${String(C).padStart(2, '0')}″`;
};
/** "00h42m44s", "0 42 44", "0:42:44", "0.712" (hours) or "10.68d" for RA; "+41°16′09″", "41 16 9", "41.27" for Dec. */
function parseAngle(text, hours) {
  let s = String(text ?? '').trim().toLowerCase();
  if (!s) return NaN_;
  let degrees = false;
  if (hours && /[d°]\s*$/.test(s) && !/[hm]/.test(s)) { degrees = true; s = s.replace(/[d°]\s*$/, ''); }
  const neg = /^\s*-/.test(s);
  const parts = s.replace(/^[+-]/, '').split(/[^0-9.]+/).filter(Boolean).map(Number);
  if (parts.length === 0 || parts.some(p => !fin(p))) return NaN_;
  let v = parts[0] + (parts[1] ?? 0) / 60 + (parts[2] ?? 0) / 3600;
  if (neg) v = -v;
  if (degrees) v /= 15;
  return v;
}

/** Calls a command (in, CommandResult out) and says what happened; a failure is shown as a toast. */
async function command(id, input = null, { quiet = false, timeout } = {}) {
  let r;
  try {
    const answers = await leaf.call(id, input, timeout ? { timeout } : {});
    r = answers === null ? { Ok: false, Error: leaf.lastProblem || 'the station did not understand that' }
      : answers.length === 0 ? { Ok: false, Error: 'nobody answered' } : answers.find(a => !a.Ok) ?? answers[0];
  } catch (e) { r = { Ok: false, Error: String(e.message ?? e) }; }
  if (!r.Ok && !quiet) toast(r.Error || 'that did not work', 'bad');
  return r;
}
/** Calls a function and gives the first answer, or null. */
async function ask(id, input = null, timeout) {
  try {
    const answers = await leaf.call(id, input, timeout ? { timeout } : {});
    if (answers === null) console.warn(id, leaf.lastProblem);
    return answers?.[0] ?? null;
  } catch (e) { console.warn(id, e); return null; }
}
/** The latest value of something that announces its state on an event and answers GetState; retried until the network knows it. */
const follows = [];
async function follow(stateId, getId, apply) {
  follows.push({ getId, apply });
  let hooked = false, seeded = false;
  for (let attempt = 0; attempt < 90 && !(hooked && seeded); attempt++) {
    // an event nobody has fired yet has no agreed type: hooking it fails until it has; the state is still asked for
    if (!hooked) try { await leaf.hook(stateId, apply); hooked = true; } catch { }
    if (!seeded) { const s = await ask(getId); if (s) { apply(s); seeded = true; } }
    if (!(hooked && seeded)) await sleep(attempt < 10 ? 2000 : 20000);
    else return;
  }
}
async function reseed() { for (const f of follows) ask(f.getId).then(s => s && f.apply(s)); }

function toast(text, kind = '') {
  const el = document.createElement('div');
  el.className = 'toast ' + kind; el.textContent = text;
  $('toasts').append(el);
  setTimeout(() => el.classList.add('gone'), kind === 'bad' ? 7000 : 3500);
  setTimeout(() => el.remove(), kind === 'bad' ? 7600 : 4100);
}
async function busy(button, job) {
  if (button.disabled) return;
  button.disabled = true;
  try { return await job(); } finally { button.disabled = false; }
}
const store = {
  get(k, fallback) { try { const v = localStorage.getItem('elink.' + k); return v == null ? fallback : JSON.parse(v); } catch { return fallback; } },
  set(k, v) { try { localStorage.setItem('elink.' + k, JSON.stringify(v)); } catch { } },
};

const phaseClass = p => ['Error', 'Failed'].includes(p) ? 'bad' : ['Idle', 'Done', 'OnTarget', 'Aborted', 'Stopped', '—', ''].includes(p) ? '' : p === 'Paused' || p.startsWith('Waiting') ? 'warn' : 'busy';
const ACTIVE = ['Running', 'Paused', 'WaitingForSky', 'WaitingForWeather', 'Starting', 'Resuming'];

// ---------------------------------------------------------------------------------------------------- tabs, red light
function showTab(name) {
  for (const b of document.querySelectorAll('#tabs [role=tab]')) b.setAttribute('aria-selected', String(b.dataset.tab === name));
  for (const p of document.querySelectorAll('[data-page]')) p.hidden = p.dataset.page !== name;
  store.set('tab', name);
  if (name === 'now') requestAnimationFrame(drawNow);
  if (name === 'plan') { requestAnimationFrame(drawPlan); planSoon(); }
  if (name === 'kept') loadKept();
  if (name === 'queue') loadQueue();
  if (name === 'rig') loadRig();
}
for (const b of document.querySelectorAll('#tabs [role=tab]')) b.onclick = () => showTab(b.dataset.tab);
$('red').onclick = () => { document.body.classList.toggle('red'); store.set('red', document.body.classList.contains('red')); };
if (store.get('red', false)) document.body.classList.add('red');

// ---------------------------------------------------------------------------------------------------- the coverage map
const css = name => getComputedStyle(document.body).getPropertyValue(name).trim();
/** Sizes a canvas to its box (and the area's shape) and gives its 2D context in CSS pixels. */
function fit(canvas, aspect) {
  const dpr = devicePixelRatio || 1, w = Math.max(200, canvas.parentElement.clientWidth);
  const h = Math.min(Math.round(w / (aspect || 1.5)), Math.round(innerHeight * 0.6));
  canvas.style.height = h + 'px';
  if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) { canvas.width = Math.round(w * dpr); canvas.height = Math.round(h * dpr); }
  const g = canvas.getContext('2d');
  g.setTransform(dpr, 0, 0, dpr, 0, 0);
  return { g, w, h };
}
/** The corners of a scope's frames placed at (x, y) in the area's own axes (degrees), as the planner places them. */
function footprints(x, y, frames, areaPa) {
  const field = -areaPa * Math.PI / 180, c = Math.cos(field), s = Math.sin(field);
  return frames.map(f => {
    const ox = f.OffsetEastDegrees * c + f.OffsetNorthDegrees * s, oy = -f.OffsetEastDegrees * s + f.OffsetNorthDegrees * c;
    const cx = x + ox, cy = y + oy, t = field + f.RotationDegrees * Math.PI / 180, ct = Math.cos(t), st = Math.sin(t);
    const hw = f.WidthDegrees / 2, hh = f.HeightDegrees / 2;
    return [[-hw, -hh], [hw, -hh], [hw, hh], [-hw, hh]].map(([lx, ly]) => [cx + lx * ct + ly * st, cy - lx * st + ly * ct]);
  });
}
const mapCache = { key: '', canvas: null };
function mapImage(bytes, cols, rows) {
  const key = cols + 'x' + rows + ':' + bytes.length + ':' + hashBytes(bytes);
  if (mapCache.key === key) return mapCache.canvas;
  const c = document.createElement('canvas'); c.width = cols; c.height = rows;
  const g = c.getContext('2d'), img = g.createImageData(cols, rows);
  const red = document.body.classList.contains('red');
  for (let i = 0; i < cols * rows; i++) {
    const v = bytes[i] / 255;
    let r, gg, b;
    if (v <= 0) [r, gg, b] = [22, 28, 40];
    else if (v < 0.5) { const t = v / 0.5; [r, gg, b] = [30 + 10 * t, 60 + 60 * t, 110 + 90 * t]; }
    else if (v < 1) { const t = (v - 0.5) / 0.5; [r, gg, b] = [40 + 20 * t, 120 + 70 * t, 200 - 80 * t]; }
    else [r, gg, b] = [80, 200, 120];
    if (red) [r, gg, b] = [Math.max(r, gg, b) * 0.9, 0, 0];
    img.data.set([r, gg, b, 255], i * 4);
  }
  g.putImageData(img, 0, 0);
  mapCache.key = key; mapCache.canvas = c;
  return c;
}
function hashBytes(b) { let h = 0; for (let i = 0; i < b.length; i += 7) h = (h * 31 + b[i]) | 0; return h; }

function drawArea(canvas, W, H, paint) {
  const { g, w, h } = fit(canvas, W > 0 && H > 0 ? W / H : 1.5);
  g.clearRect(0, 0, w, h);
  const pad = 6, sc = Math.min((w - 2 * pad) / W, (h - 2 * pad) / H), aw = W * sc, ah = H * sc, x0 = (w - aw) / 2, y0 = (h - ah) / 2;
  const P = ([x, y]) => [x0 + (x + W / 2) * sc, y0 + (H / 2 - y) * sc];
  paint({ g, P, x0, y0, aw, ah, sc });
  g.strokeStyle = css('--line-strong'); g.lineWidth = 1; g.strokeRect(x0 + .5, y0 + .5, aw - 1, ah - 1);
}
function poly(g, pts, P) { g.beginPath(); pts.forEach((p, i) => { const [x, y] = P(p); i ? g.lineTo(x, y) : g.moveTo(x, y); }); g.closePath(); }

// ---------------------------------------------------------------------------------------------------- the image (Now)
let image = null;            // the latest ImagingState
let imagePa = 0;             // position angle of the running image (from the kept list)
const rate = [];             // (time, mean seconds, visits) samples: how fast depth grows
let lastVisitsAt = 0, lastVisits = -1, lastPhase = '';

function showImage(s) {
  image = s;
  if (s.Visits !== lastVisits) { lastVisits = s.Visits; lastVisitsAt = Date.now(); }
  const now = Date.now();
  if (!rate.length || now - rate[rate.length - 1].t > 20000 || rate[rate.length - 1].label !== s.Label) rate.push({ t: now, mean: s.MeanSeconds, visits: s.Visits, label: s.Label });
  while (rate.length > 2 && (now - rate[0].t > 40 * 60000 || rate[0].label !== s.Label)) rate.shift();
  if (ACTIVE.includes(lastPhase) && s.Phase === 'Done') toast(`'${s.Label}' is done: ${s.Visits} shots`, 'ok');
  if (lastPhase !== s.Phase && s.Phase === 'Error') { toast(s.Message || 'the image stopped with an error', 'bad'); buzz(); }
  for (const w of s.Workers ?? []) { if (w.Phase === 'Failed' && failed.get(w.ScopeId) !== 'Failed') { toast(`${w.ScopeId} failed: ${w.Message}`, 'bad'); buzz(); } failed.set(w.ScopeId, w.Phase); }
  if (lastPhase !== s.Phase && !ACTIVE.includes(lastPhase) && ACTIVE.includes(s.Phase)) paOfRunning();
  lastPhase = s.Phase;

  const active = ACTIVE.includes(s.Phase);
  $('imageLabel').textContent = s.Label ? s.Label : 'No image running';
  $('imagePhase').textContent = phaseName(s.Phase);
  $('imagePhase').className = 'chip ' + phaseClass(s.Phase);
  $('imageMessage').textContent = s.Message || (active ? '' : 'Start one on the New image page, or continue a kept one.');
  const part = s.TargetSeconds > 0 ? Math.min(1, s.MeanSeconds / s.TargetSeconds) : 0;
  $('imageBar').style.width = (part * 100).toFixed(1) + '%';
  $('pause').hidden = s.Phase !== 'Running' && !s.Phase.startsWith('Waiting');
  $('resume').hidden = s.Phase !== 'Paused';
  $('stop').hidden = !active;
  $('depthRow').hidden = !active;
  if (document.activeElement !== $('depthNow')) $('depthNow').value = s.TargetSeconds > 0 ? Math.round(s.TargetSeconds / 60) : '';
  stats($('imageStats'), imageStats(s));
  const hl = $('headline');
  hl.hidden = !active && s.Phase !== 'Error';
  hl.textContent = `${s.Label}: ${s.TargetSeconds > 0 ? Math.round(part * 100) + '%' : s.Visits + ' shots'}${s.Phase !== 'Running' ? ' · ' + phaseName(s.Phase) : ''}`;
  hl.className = 'chip ' + phaseClass(s.Phase);
  document.title = active ? `${Math.round(part * 100)}% ${s.Label} · ELink` : 'ELink';
  if (!$('image').closest('[data-page]').hidden) drawNow();
}
const failed = new Map();
const buzz = () => { try { navigator.vibrate?.([300, 150, 300]); } catch { } };
const phaseName = p => ({ WaitingForSky: 'waiting for sky', WaitingForWeather: 'waiting for weather', Resuming: 'carrying on after a restart' }[p] ?? p);

function imageStats(s) {
  const out = [];
  if (!s.Label) return out;
  out.push(['Shots', String(s.Visits)]);
  if (s.TargetSeconds > 0) {
    out.push(['Depth', `${duration(s.MeanSeconds)} of ${duration(s.TargetSeconds)}`]);
    out.push(['Thinnest spot', duration(s.MinSeconds)]);
  } else {
    out.push(['Depth', `${duration(s.MeanSeconds)} on average, until you stop it`]);
    out.push(['Thinnest spot', s.MinSeconds > 0 ? duration(s.MinSeconds) : 'not reached yet (the first pass is still going)']);
  }
  for (const l of s.Layers ?? []) if ((s.Layers.length > 1) && l.Label) out.push([`Layer ${l.Label}`, `${duration(l.MeanSeconds)} of ${duration(l.TargetSeconds)}`]);
  // the pace: depth gained per hour over the last half hour or so
  const a = rate.find(r => r.label === s.Label), b = rate[rate.length - 1];
  if (a && b && b.t - a.t > 4 * 60000 && b.visits > a.visits) {
    const perHour = (b.visits - a.visits) / ((b.t - a.t) / 3600000);
    out.push(['Pace', `${Math.round(perHour)} shots/h`]);
    const gain = (b.mean - a.mean) / ((b.t - a.t) / 1000);
    if (!(s.TargetSeconds > 0) && gain > 0) out.push(['Gaining', `${duration(gain * 3600)} of depth an hour`]);
    if (s.TargetSeconds > 0 && gain > 0 && s.Phase === 'Running') {
      const left = (s.TargetSeconds - s.MeanSeconds) / gain * 1.1;     // the last thin spots take longer than the average
      out.push(['Done at', `about ${clock(new Date(Date.now() + left * 1000))} (${duration(left)})`]);
    }
  }
  if (ACTIVE.includes(s.Phase) && lastVisitsAt) {
    const ago = (Date.now() - lastVisitsAt) / 1000;
    out.push(['Last shot', ago < 90 ? Math.round(ago) + ' s ago' : duration(ago) + ' ago', ago > 600 && s.Phase === 'Running' ? 'warn' : '']);
  }
  for (const w of s.Workers ?? []) out.push([w.ScopeId, `${w.Phase}${w.Rejected ? ` · ${w.Rejected} rejected` : ''}${w.Message ? ' · ' + w.Message : ''}`, w.Phase === 'Failed' ? 'bad' : '']);
  return out;
}
function stats(dl, rows) {
  dl.textContent = '';
  for (const [k, v, cls] of rows) {
    const dt = document.createElement('dt'); dt.textContent = k;
    const dd = document.createElement('dd'); dd.textContent = v; if (cls) dd.className = cls;
    dl.append(dt, dd);
  }
}
function drawNow() {
  const s = image, canvas = $('cover');
  if (!s || !(s.WidthDegrees > 0)) { canvas.parentElement.hidden = true; return; }
  canvas.parentElement.hidden = false;
  // all the sky the frames reached (the edge frames hang over the area), with the planned area outlined in it
  const all = s.GatheredCols > 0 && s.GatheredWidthDegrees > 0;
  const W = all ? s.GatheredWidthDegrees : s.WidthDegrees, H = all ? s.GatheredHeightDegrees : s.HeightDegrees;
  drawArea(canvas, W, H, ({ g, P, x0, y0, aw, ah }) => {
    const map = blob(all ? s.Gathered : s.Map), cols = all ? s.GatheredCols : s.MapCols, rows = all ? s.GatheredRows : s.MapRows;
    if (cols > 0 && map.length === cols * rows) {
      g.imageSmoothingEnabled = false;
      g.drawImage(mapImage(map, cols, rows), x0, y0, aw, ah);
    } else { g.fillStyle = css('--panel2'); g.fillRect(x0, y0, aw, ah); }
    if (all) {
      const [ax, ay] = P([-s.WidthDegrees / 2, s.HeightDegrees / 2]), [bx, by] = P([s.WidthDegrees / 2, -s.HeightDegrees / 2]);
      g.setLineDash([6, 4]); g.strokeStyle = css('--text'); g.lineWidth = 1.5;
      g.strokeRect(Math.round(ax) + .5, Math.round(ay) + .5, Math.round(bx - ax), Math.round(by - ay));
      g.setLineDash([]);
    }
    for (const w of s.Workers ?? []) {
      if (!fin(w.PoseX) || !fin(w.PoseY)) continue;
      for (const pts of footprints(w.PoseX, w.PoseY, w.Frames ?? [], imagePa)) {
        poly(g, pts, P);
        g.fillStyle = 'rgba(255,255,255,.12)'; g.fill();
        g.strokeStyle = css('--frame'); g.lineWidth = 2; g.stroke();
      }
    }
  });
}
async function paOfRunning() {
  const kept = await ask(`${IDS.imaging}.ListSaved`);
  const k = kept?.Images?.find(i => i.Label === image?.Label);
  if (k) { imagePa = k.Request.PositionAngleDegrees; drawNow(); }
}

$('pause').onclick = e => busy(e.target, () => command(`${IDS.imaging}.Pause`));
$('resume').onclick = e => busy(e.target, () => command(`${IDS.imaging}.Resume`));
$('stop').onclick = e => busy(e.target, async () => {
  if (!confirm('Stop this image? What was taken is kept, and it can be continued later.')) return;
  const r = await command(`${IDS.imaging}.Abort`);
  if (r.Ok) toast('stopped; the image is kept');
});
$('depthSet').onclick = e => busy(e.target, async () => {
  const m = Number($('depthNow').value);
  if (!(m >= 0)) return;
  const r = await command(`${IDS.imaging}.SetTarget`, m * 60);
  if (r.Ok) toast(`depth set to ${duration(m * 60)}`, 'ok');
});

// ---------------------------------------------------------------------------------------------------- the scopes (Now)
const scopeCards = new Map();
let composition = { Scopes: [], Trains: [], MountPointers: [], CameraShooters: [], Guiders: [] };
function ensureScope(def) {
  const id = def.Id;
  if (scopeCards.has(id)) { scopeCards.get(id).name.textContent = def.DisplayName || id; return; }
  const el = document.createElement('article');
  el.className = 'card scope';
  el.innerHTML = '<div class="row between"><h3></h3><span class="chip">Idle</span></div><p class="message"></p><div class="bar"><div></div></div><p class="hint shots"></p><div class="row"><button class="danger quiet">Stop this scope</button></div>';
  const card = { el, name: el.querySelector('h3'), phase: el.querySelector('.chip'), message: el.querySelector('.message'), bar: el.querySelector('.bar > div'), shots: el.querySelector('.shots') };
  card.name.textContent = def.DisplayName || id;
  el.querySelector('button').onclick = e => busy(e.target, async () => { if (confirm(`Stop ${def.DisplayName || id}? The image goes on with the other scopes.`)) await command(`${IDS.scope(id)}.Abort`); });
  scopeCards.set(id, card);
  $('scopes').append(el);
  follow(`${IDS.scope(id)}.State`, `${IDS.scope(id)}.GetState`, s => {
    card.phase.textContent = s.Phase;
    card.phase.className = 'chip ' + phaseClass(s.Phase);
    card.message.textContent = s.Message || '';
    const planned = s.ShotsPlanned || 0;
    card.bar.parentElement.hidden = card.shots.hidden = !(s.Observing && planned > 1);
    card.bar.style.width = planned > 0 ? Math.min(100, 100 * s.ShotsDone / planned).toFixed(0) + '%' : '0%';
    card.shots.textContent = s.Observing && planned > 1 ? `shot ${s.ShotsDone} of ${planned}` : '';
  });
}
function showComposition(snapshots) {
  composition = { Scopes: [], Trains: [], MountPointers: [], CameraShooters: [], Guiders: [] };
  for (const s of snapshots ?? []) for (const k of Object.keys(composition)) composition[k].push(...(s[k] ?? []));
  const ids = new Set(composition.Scopes.map(s => s.Id));
  for (const [id, card] of [...scopeCards]) if (!ids.has(id)) { card.el.remove(); scopeCards.delete(id); }
  composition.Scopes.forEach(ensureScope);
  planScopes();
  if (!document.querySelector('[data-page=rig]').hidden) showTrains();
}

// ---------------------------------------------------------------------------------------------------- the picture (Now)
let crop = { Left: 0, Top: 0, Width: 1, Height: 1, FlipY: false };
let pictureBusy = false, pictureDirty = false, pictureObjectUrl = null, lastPicture = 0;

async function refreshPicture() {
  if (pictureBusy) { pictureDirty = true; return; }
  pictureBusy = true;
  try {
    do {
      pictureDirty = false;
      const width = Math.min(1800, Math.max(600, Math.round(innerWidth * (devicePixelRatio || 1))));
      const a = await ask(IDS.process, { Settings: D.ProcessSettings(), MaxWidth: width, MaxHeight: width, Source: '', OutOfFocusWeight: 0, PseudoOutput: '' }, 120000);
      if (!a || !a.Ok) { if (!pictureObjectUrl) $('pictureEmpty').textContent = a?.Message || 'Nothing stacked yet. The picture appears as the first frames land.'; continue; }
      const url = URL.createObjectURL(new Blob([blob(a.Png)], { type: 'image/png' }));
      crop = { Left: a.CropLeft, Top: a.CropTop, Width: Math.max(a.CropWidth, 1e-6), Height: Math.max(a.CropHeight, 1e-6), FlipY: a.FlipY };
      const img = $('pictureImg'), old = pictureObjectUrl;
      img.hidden = false; $('pictureEmpty').hidden = true;
      img.src = pictureObjectUrl = url;
      if (old) setTimeout(() => URL.revokeObjectURL(old), 3000);
      $('pictureInfo').textContent = `${a.Frames} frame${a.Frames === 1 ? '' : 's'} · ${duration(a.ExposureSeconds)} · ${a.Width}×${a.Height}`;
      lastPicture = Date.now();
    } while (pictureDirty);
  } catch (e) { $('pictureEmpty').textContent = 'could not make the picture: ' + (e.message ?? e); }
  finally { pictureBusy = false; }
}
let pictureTimer = null;
function pictureSoon() {                 // frames land every half minute or so; the picture is remade at most every 20 s
  if (pictureTimer || !$('pictureAuto').checked || document.hidden) return;
  pictureTimer = setTimeout(() => { pictureTimer = null; refreshPicture(); }, Math.max(1500, 20000 - (Date.now() - lastPicture)));
}
/** A flash where the newest frame landed, fading out (the grid position of a frame -> its place on the cropped picture). */
function flash(e) {
  const P = (x, y) => { const ny = (y - crop.Top) / crop.Height; return [(x - crop.Left) / crop.Width, crop.FlipY ? 1 - ny : ny]; };
  const pts = [P(e.X0, e.Y0), P(e.X1, e.Y1), P(e.X2, e.Y2), P(e.X3, e.Y3)];
  if (pts.some(p => !fin(p[0]) || !fin(p[1]))) return;
  const p = document.createElementNS('http://www.w3.org/2000/svg', 'polygon');
  p.setAttribute('points', pts.map(q => q.join(',')).join(' '));
  p.setAttribute('class', 'flash');
  $('landing').append(p);
  p.addEventListener('animationend', () => p.remove());
  setTimeout(() => p.remove(), 3000);
}
$('picture').onclick = () => $('picture').classList.toggle('full');
$('pictureRefresh').onclick = e => busy(e.target, refreshPicture);
$('pictureAuto').checked = store.get('pictureAuto', true);
$('pictureAuto').onchange = () => store.set('pictureAuto', $('pictureAuto').checked);
addEventListener('keydown', e => { if (e.key === 'Escape') $('picture').classList.remove('full'); });

// ---------------------------------------------------------------------------------------------------- new image
const form = $('planForm');
const F = name => form.elements[name];
let targets = null, picked = null;        // the catalogue, and the object picked from it
fetch('targets.json').then(r => r.json()).then(t => { targets = t.map(([Id, Name, Ra, Dec, Mag, Major, Minor, Pa, Kind]) => ({ Id, Name, Ra, Dec, Mag, Major, Minor, Pa, Kind, key: norm(Id + ' ' + Name) })); }).catch(ignore);
const norm = s => s.toLowerCase().replace(/[^a-z0-9]+/g, ' ').replace(/\b(ngc|ic|m|sh2|c|b|vdb|ldn|lbn) (\d)/g, '$1$2').trim();

$('find').oninput = () => {
  const q = norm($('find').value), list = $('found');
  list.textContent = '';
  if (!q || !targets) { list.hidden = true; return; }
  const words = q.split(' ');
  const hits = targets.filter(t => words.every(w => t.key.includes(w)))
    .sort((a, b) => (b.key.startsWith(q) - a.key.startsWith(q)) || (a.Mag || 99) - (b.Mag || 99)).slice(0, 12);
  for (const t of hits) {
    const li = document.createElement('li');
    li.setAttribute('role', 'option'); li.tabIndex = 0;
    li.innerHTML = `<b>${esc(t.Id)}</b> ${esc(t.Name)} <span class="hint">${esc(t.Kind)}${t.Major ? ` · ${t.Major >= 60 ? (t.Major / 60).toFixed(1) + '°' : Math.round(t.Major) + '′'}` : ''}${t.Mag ? ` · mag ${t.Mag}` : ''}</span>`;
    li.onclick = li.onkeydown = e => { if (e.type === 'keydown' && e.key !== 'Enter') return; pick(t); };
    list.append(li);
  }
  list.hidden = hits.length === 0;
};
$('find').onkeydown = e => { if (e.key === 'Enter') { e.preventDefault(); $('found').firstElementChild?.click(); } if (e.key === 'Escape') $('found').hidden = true; };
function pick(t) {
  picked = t;
  F('Label').value = t.Name ? `${t.Id} ${t.Name}`.slice(0, 40) : t.Id;
  F('Ra').value = hms(t.Ra, true); F('Dec').value = hms(t.Dec, false);
  if (!(Number(F('Width').value) > 0)) fitObject();
  $('find').value = ''; $('found').hidden = true;
  planChanged();
}
function fitObject() {
  const t = picked;
  if (!t || !(t.Major > 0)) { toast('pick an object from the search first'); return; }
  const major = t.Major / 60, minor = (t.Minor > 0 ? t.Minor : t.Major) / 60;
  // the long axis along the width; a margin around it, and never smaller than one frame
  const fr = frameOfScopes();
  F('Width').value = Math.max(major * 1.4, fr?.w ?? 0).toFixed(2);
  F('Height').value = Math.max(minor * 1.6, fr?.h ?? 0).toFixed(2);
  F('Pa').value = (((t.Pa || 0) + 90) % 180).toFixed(1);
  planChanged();
}
$('fitObject').onclick = fitObject;
$('lensApply').onclick = () => {
  const mm = Number($('lensMm').value), [sw, sh] = $('lensSensor').value.split('x').map(Number);
  if (!(mm > 0)) return;
  F('Width').value = (2 * Math.atan(sw / 2 / mm) * 180 / Math.PI).toFixed(2);
  F('Height').value = (2 * Math.atan(sh / 2 / mm) * 180 / Math.PI).toFixed(2);
  planChanged();
};
$('matchCamera').onclick = async () => {
  const a = await cameraAngle();
  if (!fin(a)) { toast('the camera angle is not known yet: it is learned from the first plate solve'); return; }
  F('Pa').value = (((a % 180) + 180) % 180).toFixed(1);
  toast('frames now line up with the area: fewer shots for the same coverage');
  planChanged();
};
/** Where the chosen scopes' imaging camera points (east of north), from the train's state. */
async function cameraAngle() {
  for (const sid of chosenScopes()) {
    const def = composition.Scopes.find(s => s.Id === sid);
    for (const sh of def?.Shooters ?? []) {
      const t = await ask(`${IDS.train(sh.Id)}.GetState`);
      const c = t?.Cameras?.find(c => c.Role === 'Imaging' && fin(c.AngleDegrees));
      if (c) return c.AngleDegrees;
    }
  }
  return NaN_;
}

function planScopes() {
  const box = $('planScopes'), was = new Set(store.get('planScopes', null) ?? composition.Scopes.map(s => s.Id));
  const checked = new Set([...box.querySelectorAll('input:checked')].map(i => i.value));
  box.textContent = '';
  if (composition.Scopes.length === 0) { box.innerHTML = '<p class="hint">No scopes yet: set one up on the Rig page.</p>'; return; }
  for (const s of composition.Scopes) {
    const l = document.createElement('label'); l.className = 'check';
    l.innerHTML = `<input type="checkbox" value="${esc(s.Id)}"> ${esc(s.DisplayName || s.Id)}`;
    l.firstChild.checked = checked.size ? checked.has(s.Id) : was.has(s.Id);
    l.firstChild.onchange = () => { store.set('planScopes', chosenScopes()); planChanged(); };
    box.append(l);
  }
}
const chosenScopes = () => [...$('planScopes').querySelectorAll('input:checked')].map(i => i.value);

const PLAN_DEFAULTS = { Label: '', Ra: '', Dec: '', Width: '', Height: '', Pa: '0', Exposure: '30', Depth: '60', Filter: '', Gain: '', Iso: '', Bin: '0', Scale: '', LiveStack: true, Stepover: '0', Dither: '30', MaxVisits: '0', SkyWait: '120', SlewTimeout: '300', Resume: true, Endless: false };
function formValues() { const o = {}; for (const k of Object.keys(PLAN_DEFAULTS)) { const el = F(k); o[k] = el.type === 'checkbox' ? el.checked : el.value; } return o; }
function endlessUi() { F('Depth').disabled = F('Endless').checked; }
function setForm(o) { for (const k of Object.keys(PLAN_DEFAULTS)) { const el = F(k); const v = o[k] ?? PLAN_DEFAULTS[k]; if (el.type === 'checkbox') el.checked = !!v; else el.value = v; } }
setForm(store.get('plan', PLAN_DEFAULTS)); endlessUi();
F('Endless').addEventListener('change', endlessUi);

/** The form as an ImagingRequest, or a reason it is not one yet. */
function request() {
  const v = formValues(), n = k => Number(v[k]);
  const ra = parseAngle(v.Ra, true), dec = parseAngle(v.Dec, false);
  if (!v.Label.trim()) return { problem: 'give the image a name' };
  if (!(ra >= 0 && ra < 24)) return { problem: 'the RA is not a time between 0h and 24h' };
  if (!(Math.abs(dec) <= 90)) return { problem: 'the Dec is not between -90° and +90°' };
  if (!(n('Exposure') > 0)) return { problem: 'an exposure of more than 0 s' };
  const scopes = chosenScopes();
  if (scopes.length === 0) return { problem: 'choose a scope' };
  // binning: a camera bins in whole steps; a fraction (1.5) is the image made at that many times the frames' scale, by the
  // stack's resampling (the subs are kept as the camera took them, to be stacked again at any scale)
  const binText = String(v.Bin ?? '').trim(), binN = binText === '' ? 0 : Number(binText);
  if (!(binN >= 0 && binN <= 16)) return { problem: "binning is 0 (the camera's), a whole number, or a fraction like 1.5" };
  const bin = Number.isInteger(binN) ? binN : 0, soft = Number.isInteger(binN) ? 0 : binN;
  if (soft && soft < 1) return { problem: 'a fractional binning is more than 1 (1.5, 2.5, ...)' };
  if (soft && !(n('Scale') > 0) && !(frameScale > 0)) return { problem: `the frames' scale is not known yet, so ${soft}× cannot be worked out: connect the camera, or give a Scale` };
  const scale = n('Scale') > 0 ? n('Scale') : soft ? Math.round(frameScale * soft * 1000) / 1000 : 0;
  return {
    req: make('ImagingRequest', {
      Label: v.Label.trim(), Center: { RaHours: ra, DecDegrees: dec, Epoch: 'J2000' },
      WidthDegrees: n('Width') || 0, HeightDegrees: n('Height') || 0, PositionAngleDegrees: n('Pa') || 0, ScopeIds: scopes,
      Exposure: { Seconds: n('Exposure'), Filter: v.Filter.trim(), BinX: bin, BinY: bin, Gain: v.Gain === '' ? NaN_ : n('Gain'), Iso: v.Iso.trim() },
      TargetSeconds: v.Endless ? 0 : (n('Depth') || 0) * 60, StepoverDegrees: n('Stepover') || 0, DitherArcsec: n('Dither') || 0, MaxVisits: n('MaxVisits') | 0,
      LiveStack: v.LiveStack, OutputPixelScaleArcsec: scale, SlewTimeoutSeconds: n('SlewTimeout') || 300, SkyWaitSeconds: n('SkyWait') || 0, Resume: v.Resume,
    }),
  };
}
function loadRequest(r) {
  setForm({
    Label: r.Label, Ra: hms(r.Center.RaHours, true), Dec: hms(r.Center.DecDegrees, false), Width: r.WidthDegrees || '', Height: r.HeightDegrees || '', Pa: r.PositionAngleDegrees,
    Exposure: r.Exposure.Seconds, Depth: r.TargetSeconds ? Math.round(r.TargetSeconds / 60) : 60, Endless: !(r.TargetSeconds > 0), Filter: r.Exposure.Filter, Gain: fin(r.Exposure.Gain) ? r.Exposure.Gain : '', Iso: r.Exposure.Iso,
    Bin: String(r.Exposure.BinX || 0), Scale: r.OutputPixelScaleArcsec || '', LiveStack: r.LiveStack, Stepover: r.StepoverDegrees, Dither: r.DitherArcsec, MaxVisits: r.MaxVisits,
    SkyWait: r.SkyWaitSeconds, SlewTimeout: r.SlewTimeoutSeconds, Resume: true,
  });
  for (const i of $('planScopes').querySelectorAll('input')) i.checked = r.ScopeIds.includes(i.value) || r.ScopeIds.length === 0;
  picked = null; endlessUi(); planChanged();
}

let preview = null, planTimer = null, planSeq = 0;
/** The finest pixel scale of the chosen scopes' imaging cameras (″/px), for a fractional binning; NaN until known. */
let frameScale = NaN_;
async function refreshFrameScale() {
  let finest = NaN_;
  for (const sid of chosenScopes()) {
    const def = composition.Scopes.find(s => s.Id === sid);
    for (const sh of def?.Shooters ?? []) {
      const t = await ask(`${IDS.train(sh.Id)}.GetState`);
      for (const c of t?.Cameras ?? []) if (c.Role === 'Imaging' && c.PixelScaleArcsec > 0 && !(c.PixelScaleArcsec >= finest)) finest = c.PixelScaleArcsec;
    }
  }
  const was = frameScale; frameScale = finest;
  if (finest !== was && !Number.isInteger(Number(F('Bin').value))) planSoon();
}
function planChanged() { store.set('plan', formValues()); outSize(); refreshFrameScale().catch(ignore); planSoon(); }
function planSoon() { clearTimeout(planTimer); planTimer = setTimeout(runPreview, 400); }
form.addEventListener('input', e => { if (e.target.id !== 'find' && e.target.id !== 'lensMm' && e.target.id !== 'mp') planChanged(); });
form.addEventListener('change', e => { if (e.target.id !== 'find' && e.target.id !== 'mp') planChanged(); });
$('mp').oninput = () => {
  const mp = Number($('mp').value), W = Number(F('Width').value), H = Number(F('Height').value);
  if (mp > 0 && W > 0 && H > 0) { F('Scale').value = (Math.sqrt(W * H * 3600 * 3600 / (mp * 1e6))).toFixed(2); planChanged(); }
};
function outSize() {
  const W = Number(F('Width').value), H = Number(F('Height').value), s = Number(F('Scale').value);
  if (W > 0 && H > 0 && s > 0) {
    const w = Math.round(W * 3600 / s), h = Math.round(H * 3600 / s);
    // the stack holds 3 colour sums, a weight and a sum of squares per pixel: 20 bytes
    const mb = Math.round(w * h * 20 / 1e6);
    $('outSize').textContent = `The picture: ${w} × ${h} px, ${(w * h / 1e6).toFixed(1)} MP, about ${mb} MB of stack in memory${mb > 300 ? ': too much for a 2 GB Raspberry Pi, pick a coarser scale or fewer MP' : ''}.`;
    $('outSize').className = 'hint' + (mb > 300 ? ' warn' : '');
  } else $('outSize').textContent = s > 0 ? '' : 'At the camera\'s own scale (0): for a wide area pick a coarser scale, the stack is far smaller.';
}
outSize();

async function runPreview() {
  const { req, problem } = request();
  const seq = ++planSeq;
  if (!req) { preview = null; $('planInfo').textContent = problem; drawPlan(); return; }
  const p = await ask(`${IDS.imaging}.Preview`, req, 20000);
  if (seq !== planSeq) return;
  preview = p && p.Phase !== 'Error' ? { state: p, req } : null;
  if (!p) $('planInfo').textContent = 'no imaging service answered';
  else if (p.Phase === 'Error') $('planInfo').textContent = p.Message;
  else $('planInfo').textContent = p.Message + estimate(p, req);
  drawPlan();
  observability(req);
}
/** About how many shots and hours: the ideal (frames to tile the area, times the depth) and what the planner tends to need. */
function estimate(p, req) {
  const frames = p.Workers.flatMap(w => w.Frames.map(f => ({ a: f.WidthDegrees * f.HeightDegrees, w })));
  if (!frames.length) return '';
  if (!(req.TargetSeconds > 0)) return '. It keeps going until you stop it: each pass of about an hour deepens all of it evenly.';
  const area = p.WidthDegrees * p.HeightDegrees, perShot = frames.reduce((s, f) => s + f.a, 0);
  const tiles = area / perShot, deep = Math.ceil(req.TargetSeconds / req.Exposure.Seconds);
  const scopes = Math.max(1, p.Workers.length);
  // frames square to the area tile it with less overlap (measured: 1.4x the ideal lined up, 1.6x at 35°)
  const f0 = p.Workers[0].Frames[0], rel = (((f0.RotationDegrees - req.PositionAngleDegrees) % 90) + 90) % 90;
  const square = Math.min(rel, 90 - rel) < 3;
  const lo = Math.ceil(tiles * deep * (square ? 1.3 : 1.45)), hi = Math.ceil(tiles * deep * (square ? 1.45 : 1.7));
  const pace = req.Exposure.Seconds + 3;          // each shot: the exposure, a short hop, the download
  const total = lo * pace / scopes, first = Math.min(total, Math.max(tiles * 1.5 * pace, 1.4 * 3600) / scopes);
  return `. About ${lo}–${hi} shots, ${duration(lo * pace / scopes)}–${duration(hi * pace / scopes)}${square ? '' : ' (Match camera angle needs fewer)'}`
    + (total > 2 * first ? `; it is taken in thin passes, so there is data everywhere after about ${duration(first)}.` : '.');
}
function frameOfScopes() {
  const f = preview?.state?.Workers?.[0]?.Frames?.[0];
  return f ? { w: f.WidthDegrees, h: f.HeightDegrees } : null;
}
function drawPlan() {
  const canvas = $('planCanvas');
  const p = preview?.state;
  if (!p || !(p.WidthDegrees > 0)) { canvas.parentElement.hidden = true; return; }
  canvas.parentElement.hidden = false;
  const pa = preview.req.PositionAngleDegrees;
  drawArea(canvas, p.WidthDegrees, p.HeightDegrees, ({ g, P, x0, y0, aw, ah }) => {
    g.fillStyle = css('--panel2'); g.fillRect(x0, y0, aw, ah);
    // a light grid of where frames would go, then one frame at the centre
    const f = p.Workers[0]?.Frames?.[0];
    if (f) {
      const t = (-pa + f.RotationDegrees) * Math.PI / 180, ax = [Math.cos(t), -Math.sin(t)], ay = [Math.sin(t), Math.cos(t)];
      const R = Math.hypot(p.WidthDegrees, p.HeightDegrees) / 2, ni = Math.ceil(R / f.WidthDegrees) + 1, nj = Math.ceil(R / f.HeightDegrees) + 1;
      g.save(); g.beginPath(); g.rect(x0, y0, aw, ah); g.clip();
      g.strokeStyle = css('--line-strong'); g.lineWidth = 1; g.setLineDash([3, 3]);
      for (let i = -ni; i <= ni; i++) for (let j = -nj; j <= nj; j++) {
        const cx = i * f.WidthDegrees * ax[0] + j * f.HeightDegrees * ay[0], cy = i * f.WidthDegrees * ax[1] + j * f.HeightDegrees * ay[1];
        if (Math.abs(cx) > p.WidthDegrees / 2 + R / 2 || Math.abs(cy) > p.HeightDegrees / 2 + R / 2) continue;
        for (const pts of footprints(cx, cy, [{ ...f, OffsetEastDegrees: 0, OffsetNorthDegrees: 0 }], pa)) { poly(g, pts, P); g.stroke(); }
      }
      g.restore();
    }
    p.Workers.forEach((w, i) => {
      for (const pts of footprints(0, 0, w.Frames, pa)) {
        poly(g, pts, P);
        g.fillStyle = i === 0 ? 'rgba(111,177,255,.18)' : 'rgba(95,208,143,.18)'; g.fill();
        g.strokeStyle = i === 0 ? css('--accent') : css('--ok'); g.lineWidth = 2; g.stroke();
      }
    });
    // north: the area's up is at PA east of north, so north on this picture is turned by -PA
    const t = -pa * Math.PI / 180, cx = x0 + aw - 22, cy = y0 + 24;
    g.strokeStyle = g.fillStyle = css('--dim'); g.lineWidth = 1.5;
    g.beginPath(); g.moveTo(cx, cy); g.lineTo(cx + 14 * Math.sin(t), cy - 14 * Math.cos(t)); g.stroke();
    g.font = '11px system-ui'; g.textAlign = 'center'; g.fillText('N', cx + 22 * Math.sin(t), cy - 22 * Math.cos(t) + 4);
    if (f) { g.fillStyle = css('--dim'); g.textAlign = 'left'; g.fillText(`${p.WidthDegrees.toFixed(2)}° × ${p.HeightDegrees.toFixed(2)}° · frame ${f.WidthDegrees.toFixed(2)}° × ${f.HeightDegrees.toFixed(2)}°`, x0 + 6, y0 + ah - 8); }
  });
}
let obsSeq = 0;
async function observability(req) {
  const seq = ++obsSeq;
  const o = await ask(`${IDS.site}.Observability`, { Target: req.Center, MinAltitudeDegrees: NaN_, FromUtc: '', Hours: 24 });
  if (seq !== obsSeq) return;
  if (!o || !o.Ok) { $('obs').textContent = o?.Message ?? ''; return; }
  const bits = [`now ${o.Altitude.toFixed(0)}° up${o.AboveHorizon ? '' : ' (below the limit)'}`];
  if (utc(o.TransitUtc)) bits.push(`highest ${o.TransitAltitude.toFixed(0)}° at ${clock(utc(o.TransitUtc))}`);
  if (utc(o.SetUtc)) bits.push(`sets ${clock(utc(o.SetUtc))}`);
  if (utc(o.RiseUtc) && !o.AboveHorizon) bits.push(`rises ${clock(utc(o.RiseUtc))}`);
  if (fin(o.DarkHoursVisible)) bits.push(`${o.DarkHoursVisible.toFixed(1)} h up in the dark`);
  if (fin(o.MoonSeparationDegrees)) bits.push(`Moon ${o.MoonSeparationDegrees.toFixed(0)}° away`);
  $('obs').textContent = bits.join(' · ');
  $('obs').className = 'hint' + (o.AboveHorizon ? '' : ' warn');
}

form.onsubmit = e => {
  e.preventDefault();
  busy($('startNow'), async () => {
    const { req, problem } = request();
    if (!req) { toast(problem, 'bad'); return; }
    if (image && ACTIVE.includes(image.Phase)) { toast(`'${image.Label}' is being taken: stop it first`, 'bad'); return; }
    toast('starting: slewing and learning the camera angle…');
    const r = await command(`${IDS.imaging}.Start`, req, { timeout: 300000 });
    if (r.Ok) { toast(`'${req.Label}' started`, 'ok'); showTab('now'); }
  });
};
$('addQueue').onclick = e => busy(e.target, async () => {
  const { req, problem } = request();
  if (!req) { toast(problem, 'bad'); return; }
  const sched = await ask(`${IDS.scheduler}.GetSchedule`);
  if (!sched) { toast('no scheduler answered', 'bad'); return; }
  const others = sched.Entries.filter(x => x.Request.Label !== req.Label);
  const old = sched.Entries.find(x => x.Request.Label === req.Label);
  others.push(old ? { ...old, Request: req } : make('ScheduleEntry', { Request: req }));
  const r = await command(`${IDS.scheduler}.SetSchedule`, { Entries: others, MinRunMinutes: sched.MinRunMinutes });
  if (r.Ok) toast(old ? `'${req.Label}' updated in the queue` : `'${req.Label}' added to the queue`, 'ok');
});
$('gotoCentre').onclick = e => busy(e.target, async () => {
  const { req, problem } = request();
  if (!req) { toast(problem, 'bad'); return; }
  const def = composition.Scopes.find(s => s.Id === req.ScopeIds[0]);
  const pointer = def?.Pointers?.[0];
  if (!pointer) { toast('the scope has no mount', 'bad'); return; }
  const r = await command(`${IDS.pointer(pointer)}.Goto`, req.Center, { timeout: 300000 });
  if (r.Ok) toast('there', 'ok');
});
$('resetForm').onclick = () => { setForm(PLAN_DEFAULTS); picked = null; planChanged(); };
addEventListener('resize', () => { drawNow(); drawPlan(); });

// ---------------------------------------------------------------------------------------------------- kept images
async function loadKept() {
  const list = $('keptList');
  const k = await ask(`${IDS.imaging}.ListSaved`);
  if (!k) { list.innerHTML = '<p class="hint">No imaging service answered.</p>'; return; }
  list.textContent = '';
  if (k.Images.length === 0) { list.innerHTML = '<p class="hint">None yet.</p>'; return; }
  for (const i of [...k.Images].sort((a, b) => String(b.UpdatedUtc).localeCompare(String(a.UpdatedUtc)))) {
    const r = i.Request, part = r.TargetSeconds > 0 ? Math.min(1, i.MeanSeconds / r.TargetSeconds) : 0;
    const el = document.createElement('div'); el.className = 'item';
    el.innerHTML = `<div class="row between"><b>${esc(i.Label)}</b><span class="hint">${esc(utc(i.UpdatedUtc)?.toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' }) ?? '')}</span></div>
      <p class="hint">${r.WidthDegrees.toFixed(2)}° × ${r.HeightDegrees.toFixed(2)}° at ${hms(r.Center.RaHours, true)} ${hms(r.Center.DecDegrees, false)} · ${i.Visits} shots of ${r.Exposure.Seconds} s · depth ${duration(i.MeanSeconds)}${r.TargetSeconds ? ` of ${duration(r.TargetSeconds)}` : ''}, thinnest ${duration(i.MinSeconds)}</p>
      <div class="bar"><div style="width:${(part * 100).toFixed(1)}%"></div></div>
      <div class="row"><button class="primary" data-a="go">Continue</button><button data-a="edit">Edit…</button><button data-a="queue">Queue</button><span class="grow"></span><button class="danger quiet" data-a="del">Delete</button></div>`;
    el.querySelector('[data-a=go]').onclick = e => busy(e.target, async () => {
      if (image && ACTIVE.includes(image.Phase)) { toast(`'${image.Label}' is being taken: stop it first`, 'bad'); return; }
      toast(`continuing '${i.Label}'…`);
      const res = await command(`${IDS.imaging}.Start`, make('ImagingRequest', { ...clone(r), Resume: true }), { timeout: 300000 });
      if (res.Ok) { toast(`'${i.Label}' carries on`, 'ok'); showTab('now'); }
    });
    el.querySelector('[data-a=edit]').onclick = () => { loadRequest(r); showTab('plan'); };
    el.querySelector('[data-a=queue]').onclick = e => busy(e.target, async () => { loadRequest(r); $('addQueue').click(); });
    el.querySelector('[data-a=del]').onclick = e => busy(e.target, async () => {
      if (!confirm(`Delete '${i.Label}'? Its coverage and stack are forgotten (the frames on disk stay).`)) return;
      const res = await command(`${IDS.imaging}.DeleteSaved`, i.Label);
      if (res.Ok) loadKept();
    });
    list.append(el);
  }
}
$('keptRefresh').onclick = e => busy(e.target, loadKept);

// ---------------------------------------------------------------------------------------------------- the queue
let schedule = null, schedState = null;
async function loadQueue() {
  const s = await ask(`${IDS.scheduler}.GetSchedule`);
  if (!s) { $('queueList').innerHTML = '<p class="hint">No scheduler answered.</p>'; return; }
  schedule = s;
  const st = await ask(`${IDS.scheduler}.GetState`); if (st) schedState = st, showSchedState(st);
  if (document.activeElement !== $('minRun')) $('minRun').value = s.MinRunMinutes;
  showQueue();
}
function showQueue() {
  const list = $('queueList');
  list.textContent = '';
  if (!schedule) return;
  if (schedule.Entries.length === 0) { list.innerHTML = '<p class="hint">Empty. Add images from the New image or Kept page; the queue runs the best one for the sky, all night.</p>'; return; }
  schedule.Entries.forEach((e, n) => {
    const st = schedState?.Entries?.find(x => x.Label === e.Request.Label);
    const el = document.createElement('div'); el.className = 'item' + (e.Enabled ? '' : ' off');
    el.innerHTML = `<div class="row between"><label class="check"><input type="checkbox" ${e.Enabled ? 'checked' : ''}> <b>${esc(e.Request.Label)}</b></label>
        <span class="chip ${st?.Status === 'Running' ? 'busy' : ''}">${esc(st?.Status ?? '')}</span></div>
      <p class="hint">${st ? `${fin(st.Altitude) ? st.Altitude.toFixed(0) + '° up · ' : ''}${fin(st.CompletePercent) ? st.CompletePercent.toFixed(0) + '% done' : ''}${st.Reason ? ' · ' + esc(st.Reason) : ''}` : ''}</p>
      <div class="row tight">
        <label class="inline">priority <input type="number" class="short" data-f="Priority" value="${e.Priority}"></label>
        <label class="inline">above <input type="number" class="short" data-f="MinAltitudeDegrees" value="${fin(e.MinAltitudeDegrees) ? e.MinAltitudeDegrees : ''}" placeholder="site">°</label>
        <label class="inline"><input type="checkbox" data-f="RequireDark" ${e.RequireDark ? 'checked' : ''}> only when dark</label>
        <span class="grow"></span>
        <button class="quiet" data-a="up" ${n === 0 ? 'disabled' : ''} aria-label="up">↑</button>
        <button class="quiet" data-a="down" ${n === schedule.Entries.length - 1 ? 'disabled' : ''} aria-label="down">↓</button>
        <button class="quiet" data-a="edit">Edit…</button>
        <button class="danger quiet" data-a="del">Remove</button>
      </div>`;
    el.querySelector('label.check input').onchange = ev => { e.Enabled = ev.target.checked; el.classList.toggle('off', !e.Enabled); dirtyQueue(); };
    for (const inp of el.querySelectorAll('[data-f]')) inp.onchange = () => {
      const f = inp.dataset.f;
      e[f] = inp.type === 'checkbox' ? inp.checked : inp.value === '' ? NaN_ : Number(inp.value);
      if (f === 'Priority') e[f] |= 0;
      dirtyQueue();
    };
    const move = d => { const a = schedule.Entries; [a[n], a[n + d]] = [a[n + d], a[n]]; dirtyQueue(); showQueue(); };
    el.querySelector('[data-a=up]').onclick = () => move(-1);
    el.querySelector('[data-a=down]').onclick = () => move(1);
    el.querySelector('[data-a=edit]').onclick = () => { loadRequest(e.Request); showTab('plan'); };
    el.querySelector('[data-a=del]').onclick = () => { schedule.Entries.splice(n, 1); dirtyQueue(); showQueue(); };
    list.append(el);
  });
}
function dirtyQueue() { $('queueSave').classList.add('primary'); $('queueSave').textContent = 'Save changes'; }
$('minRun').onchange = () => { if (schedule) { schedule.MinRunMinutes = Number($('minRun').value) || 0; dirtyQueue(); } };
$('queueSave').onclick = e => busy(e.target, async () => {
  if (!schedule) return;
  const r = await command(`${IDS.scheduler}.SetSchedule`, { Entries: schedule.Entries.map(x => make('ScheduleEntry', x)), MinRunMinutes: schedule.MinRunMinutes });
  if (r.Ok) { toast('queue saved', 'ok'); $('queueSave').classList.remove('primary'); $('queueSave').textContent = 'Save'; loadQueue(); }
});
$('schedStart').onclick = e => busy(e.target, async () => { if ((await command(`${IDS.scheduler}.Start`)).Ok) toast('the queue is running', 'ok'); });
$('schedStop').onclick = e => busy(e.target, async () => { if ((await command(`${IDS.scheduler}.Stop`)).Ok) toast('the queue stopped (the image being taken goes on until you stop it)'); });
function showSchedState(s) {
  schedState = s;
  $('schedPhase').textContent = s.Phase || 'Idle'; $('schedPhase').className = 'chip ' + phaseClass(s.Phase || '');
  $('schedMessage').textContent = (s.Running ? `running '${s.Running}'. ` : '') + (s.Message || '');
  if (!document.querySelector('[data-page=queue]').hidden && !$('queueSave').classList.contains('primary')) showQueue();
}

// ---------------------------------------------------------------------------------------------------- the rig
let devices = [];
const deviceState = new Map();
async function loadRig() {
  loadDevices(); loadProfiles(); showTrains(); loadSite(); loadStation();
  fillSelect($('blScope'), composition.Scopes.map(s => ({ Id: s.Id, DisplayName: s.DisplayName || s.Id })));
}
async function loadDevices() {
  try { devices = ((await leaf.call(IDS.devices, null)) ?? []).flatMap(l => l.Devices ?? []); } catch { devices = []; }
  devices.sort((a, b) => a.Kind.localeCompare(b.Kind) || a.Id.localeCompare(b.Id));
  const box = $('devices');
  box.textContent = '';
  if (!devices.length) box.innerHTML = '<p class="hint">No devices: start the drivers (a profile) or check the INDI server.</p>';
  for (const d of devices) {
    const el = document.createElement('div'); el.className = 'item row between';
    el.innerHTML = `<span><b>${esc(d.DisplayName || d.Id)}</b> <span class="hint">${esc(d.Kind)} · ${esc(d.Source)}</span></span><span class="row tight"><span class="chip">…</span><button>Connect</button></span>`;
    const chip = el.querySelector('.chip'), btn = el.querySelector('button');
    const show = s => { deviceState.set(d.Kind + ':' + d.Id, s); chip.textContent = s.Connected ? 'connected' : 'not connected'; chip.className = 'chip ' + (s.Connected ? 'ok' : ''); btn.textContent = s.Connected ? 'Disconnect' : 'Connect'; };
    btn.onclick = e => busy(e.target, async () => {
      const on = !deviceState.get(d.Kind + ':' + d.Id)?.Connected;
      const r = await command(`${IDS.device(d.Kind, d.Id)}.Connect`, on, { timeout: 60000 });
      const s = await ask(`${IDS.device(d.Kind, d.Id)}.GetState`); if (s) show(s);
      if (r.Ok) toast(`${d.DisplayName || d.Id} ${on ? 'connected' : 'disconnected'}`, 'ok');
    });
    box.append(el);
    ask(`${IDS.device(d.Kind, d.Id)}.GetState`).then(s => s && show(s));
    leaf.hook(`${IDS.device(d.Kind, d.Id)}.State`, show).catch(ignore);
  }
  // the mounts, for the mount card and the scope set up
  const mounts = devices.filter(d => d.Kind === 'Mount'), cams = devices.filter(d => d.Kind === 'Camera');
  fillSelect($('mountPick'), mounts, store.get('mount', mounts[0]?.Id));
  fillSelect(document.querySelector('#setupForm [name=Mount]'), mounts);
  fillSelect(document.querySelector('#setupForm [name=Camera]'), cams);
  followMount();
}
function fillSelect(sel, list, want) {
  const was = want ?? sel.value;
  sel.textContent = '';
  for (const d of list) { const o = document.createElement('option'); o.value = d.Id; o.textContent = d.DisplayName || d.Id; sel.append(o); }
  if (list.some(d => d.Id === was)) sel.value = was;
}
let mountHooked = '', mountState = null;
function showMount(s) {
  mountState = s;
  $('mountPhase').textContent = s.Connected ? s.Phase || '—' : 'not connected';
  $('mountPhase').className = 'chip ' + (s.Connected ? phaseClass(s.Phase || '') : '');
  stats($('mountStats'), [
    ['Pointing', s.Connected ? `${hms(s.RaHours, true)} ${hms(s.DecDegrees, false)} (${s.Epoch})` : '—'],
    ['Tracking', s.Tracking ? 'yes' : 'no', s.Connected && !s.Tracking && !s.Parked ? 'warn' : ''],
    ['Parked', s.Parked ? 'yes' : 'no'],
    ['Pier side', s.PierSide || '—'],
    ...(s.Message ? [['Note', s.Message]] : []),
  ]);
  $('unpark').hidden = !s.Parked; $('park').hidden = s.Parked;
  $('trackOn').hidden = s.Tracking || s.Parked; $('trackOff').hidden = !s.Tracking;
}
function followMount() {
  const id = $('mountPick').value;
  $('mountCard').hidden = !id;
  if (!id || id === mountHooked) { if (id) ask(`${IDS.device('Mount', id)}.GetState`).then(s => s && showMount(s)); return; }
  mountHooked = id;
  leaf.hook(`${IDS.device('Mount', id)}.State`, s => { if ($('mountPick').value === id) showMount(s); }).catch(ignore);
  ask(`${IDS.device('Mount', id)}.GetState`).then(s => s && showMount(s));
}
$('mountPick').onchange = () => { store.set('mount', $('mountPick').value); followMount(); };
const mountCmd = (name, input = null, timeout = 120000) => e => busy(e.target, async () => {
  const r = await command(`${IDS.device('Mount', $('mountPick').value)}.${name}`, input, { timeout });
  if (r.Ok) toast('done', 'ok');
});
$('unpark').onclick = mountCmd('Park', false);
$('park').onclick = e => { if (confirm('Park the mount? A running image would lose its pointing.')) mountCmd('Park', true)(e); };
$('trackOn').onclick = mountCmd('SetTracking', true);
$('trackOff').onclick = mountCmd('SetTracking', false);
$('mountStop').onclick = mountCmd('Abort', null, 20000);
$('devRefresh').onclick = e => busy(e.target, loadDevices);

async function loadProfiles() {
  const p = await ask(`${IDS.profiles}.List`), st = await ask(`${IDS.profiles}.GetState`);
  $('profileRow').hidden = !p || (p.Profiles.length === 0 && !st?.Running);
  if ($('profileRow').hidden) return;
  fillSelect($('profilePick'), p.Profiles.map(x => ({ Id: x.Label, DisplayName: `${x.Label} (${x.Drivers.length} driver${x.Drivers.length === 1 ? '' : 's'})` })), st?.Running || undefined);
  if (st) $('profileState').textContent = `${st.Phase}${st.Running ? ': ' + st.Running : ''}${st.Message ? ' · ' + st.Message : ''}`;
  $('profileStart').hidden = p.Profiles.length === 0;
  $('profileStop').hidden = !st?.Running;
}
$('profileStart').onclick = e => busy(e.target, async () => { if ((await command(`${IDS.profiles}.Start`, $('profilePick').value, { timeout: 60000 })).Ok) { toast('drivers starting', 'ok'); setTimeout(loadRig, 4000); } });
$('profileStop').onclick = e => busy(e.target, async () => { if (confirm('Stop the drivers? Everything disconnects.') && (await command(`${IDS.profiles}.Stop`)).Ok) setTimeout(loadRig, 2000); });

async function showTrains() {
  const box = $('trains');
  box.textContent = '';
  for (const s of composition.Scopes) {
    const el = document.createElement('div'); el.className = 'item';
    const trains = s.Shooters.map(sh => composition.Trains.find(t => t.Id === sh.Id)).filter(Boolean);
    el.innerHTML = `<div class="row between"><b>${esc(s.DisplayName || s.Id)}</b><span class="row tight"><button class="quiet" data-a="edit">Edit</button><button class="danger quiet" data-a="del">Remove</button></span></div>
      <p class="hint">${esc(s.Pointers.map(id => composition.MountPointers.find(m => m.Id === id)?.MountId ?? id).join(', ') || 'no mount')} · ${trains.map(t => `${esc(t.Label || t.Id)}: ${t.FocalLengthMm} mm, ${esc(t.Cameras.map(c => c.CameraId).join(', '))}`).join('; ') || esc(s.Shooters.map(x => x.Id).join(', '))}
      · ${s.GuiderId ? 'guided' : 'unguided'}${s.CenterAfterSlew ? ' · centres' : ''}${s.GradeFrames ? ' · grades frames' : ''}</p><p class="hint cams"></p>`;
    el.querySelector('[data-a=edit]').onclick = () => editScope(s, trains[0]);
    el.querySelector('[data-a=del]').onclick = e => busy(e.target, async () => {
      if (!confirm(`Remove the scope ${s.DisplayName || s.Id}? The devices and the telescope stay.`)) return;
      if ((await command('ELink.Compose.Remove', 'Scope:' + s.Id)).Ok) toast('removed', 'ok');
    });
    box.append(el);
    for (const t of trains) ask(`${IDS.train(t.Id)}.GetState`).then(ts => {
      if (!ts) return;
      el.querySelector('.cams').textContent = ts.Cameras.map(c => `${c.CameraId}: ${fin(c.PixelScaleArcsec) ? c.PixelScaleArcsec.toFixed(2) + '″/px, ' : ''}${fin(c.FieldWidthDegrees) ? `${c.FieldWidthDegrees.toFixed(2)}° × ${c.FieldHeightDegrees.toFixed(2)}°` : ''}${fin(c.AngleDegrees) ? `, turned ${c.AngleDegrees.toFixed(1)}°` : ''}${c.Connected ? '' : ' (not connected)'}`).join(' · ');
    });
  }
}
let editing = null;
function editScope(s, t) {
  const f = $('setupForm').elements;
  editing = { scope: s, train: t };
  f.Name.value = s.DisplayName || s.Id;
  const pointer = composition.MountPointers.find(p => p.Id === s.Pointers[0]);
  if (pointer) f.Mount.value = pointer.MountId;
  if (t) { f.Camera.value = t.Cameras[0]?.CameraId ?? f.Camera.value; f.Focal.value = t.FocalLengthMm; f.Aperture.value = t.ApertureMm || ''; f.Pixel.value = t.Cameras[0]?.PixelSizeUm || ''; }
  f.Grade.checked = s.GradeFrames; f.Center.checked = s.CenterAfterSlew; f.Flip.checked = s.MeridianFlip;
  $('setupInfo').textContent = `editing ${s.DisplayName || s.Id}`;
  $('setupForm').scrollIntoView({ behavior: 'smooth', block: 'center' });
}
const slug = s => s.toLowerCase().normalize('NFKD').replace(/[^a-z0-9]+/g, '').slice(0, 24) || 'scope';
$('setupForm').onsubmit = e => {
  e.preventDefault();
  const f = e.target.elements, btn = e.target.querySelector('button.primary');
  busy(btn, async () => {
    if (!f.Mount.value || !f.Camera.value) { toast('connect a mount and a camera first (Equipment above)', 'bad'); return; }
    const id = editing?.scope.Id ?? slug(f.Name.value);
    const trainId = editing?.train?.Id ?? 't' + id;
    // the mount as a pointer (reuse one made for this mount)
    let pointer = composition.MountPointers.find(p => p.MountId === f.Mount.value)?.Id;
    if (!pointer) {
      pointer = 'm' + slug(f.Mount.value);
      if (!(await command(IDS.definePointer, { Id: pointer, MountId: f.Mount.value })).Ok) return;
    }
    const oldCam = editing?.train?.Cameras?.find(c => c.CameraId === f.Camera.value);
    const train = make('ImagingTrainDefinition', {
      ...(editing?.train ? clone(editing.train) : {}), Id: trainId, Label: editing?.train?.Label || `${f.Focal.value} mm`, FocalLengthMm: Number(f.Focal.value), ApertureMm: Number(f.Aperture.value) || 0,
      Cameras: [make('TrainCamera', { ...(oldCam ? clone(oldCam) : {}), CameraId: f.Camera.value, Role: 'Imaging', PixelSizeUm: Number(f.Pixel.value) || 0 })],
    });
    if (!(await command(IDS.defineTrain, train)).Ok) return;
    const scope = make('ScopeDefinition', {
      ...(editing ? clone(editing.scope) : {}), Id: id, DisplayName: f.Name.value.trim(), Pointers: [pointer],
      Shooters: editing?.scope.Shooters?.length ? editing.scope.Shooters : [make('ScopeShooterRef', { Id: trainId })],
      GradeFrames: f.Grade.checked, CenterAfterSlew: f.Center.checked, MeridianFlip: f.Flip.checked,
    });
    if (!scope.Shooters.some(x => x.Id === trainId)) scope.Shooters.push(make('ScopeShooterRef', { Id: trainId }));
    if ((await command(IDS.defineScope, scope)).Ok) { toast(`${scope.DisplayName} saved`, 'ok'); editing = null; $('setupInfo').textContent = ''; }
  });
};

let site = null;
function showSite(s) {
  site = s;
  const sun = s.SunAltitude, dusk = utc(s.DuskUtc), dawn = utc(s.DawnUtc), now = utc(s.UtcNow) ?? new Date();
  let sky = s.Known ? (s.Sky || '') : 'site not set';
  if (s.Known && dawn && dusk) sky += sun < -18 ? ` · dark until ${clock(dawn)}` : ` · dark at ${clock(dusk)}`;
  if (s.Known && fin(s.MoonIllumination)) sky += ` · Moon ${Math.round(s.MoonIllumination * 100)}%${s.MoonAltitude > 0 ? ' up' : ''}`;
  $('sky').textContent = sky;
  if (!document.querySelector('[data-page=rig]').hidden) siteStats();
}
function siteStats() {
  const s = site; if (!s) return;
  stats($('siteStats'), [
    ['Sky', s.Sky || '—'], ['Sun', fin(s.SunAltitude) ? s.SunAltitude.toFixed(1) + '°' : '—'],
    ['Dark', `${clock(utc(s.DuskUtc))} – ${clock(utc(s.DawnUtc))}`],
    ['Moon', fin(s.MoonIllumination) ? `${Math.round(s.MoonIllumination * 100)}% lit, ${s.MoonAltitude.toFixed(0)}° up · rises ${clock(utc(s.MoonRiseUtc))}, sets ${clock(utc(s.MoonSetUtc))}` : '—'],
    ['Clock', fin(s.ClockOffsetSeconds) && Math.abs(s.ClockOffsetSeconds) > 2 ? `off by ${s.ClockOffsetSeconds.toFixed(0)} s` : 'fine', fin(s.ClockOffsetSeconds) && Math.abs(s.ClockOffsetSeconds) > 30 ? 'warn' : ''],
    ...(s.Message ? [['Note', s.Message]] : []),
  ]);
}
function loadSite() {
  const s = site, f = $('siteForm').elements;
  siteStats();
  if (!s || document.activeElement?.form === $('siteForm')) return;
  const c = s.Config;
  f.Label.value = c.Label; f.Lat.value = fin(c.LatitudeDegrees) ? c.LatitudeDegrees : ''; f.Lon.value = fin(c.LongitudeDegrees) ? c.LongitudeDegrees : '';
  f.Elev.value = c.ElevationMeters; f.MinAlt.value = c.MinAltitudeDegrees; f.Push.checked = c.PushToMounts;
}
$('siteForm').onsubmit = e => {
  e.preventDefault();
  const f = e.target.elements;
  busy(e.target.querySelector('button.primary'), async () => {
    const cfg = make('SiteConfig', { ...(site ? clone(site.Config) : {}), Label: f.Label.value || 'Home', LatitudeDegrees: Number(f.Lat.value), LongitudeDegrees: Number(f.Lon.value), ElevationMeters: Number(f.Elev.value) || 0, MinAltitudeDegrees: Number(f.MinAlt.value) || 0, PushToMounts: f.Push.checked });
    if ((await command(`${IDS.site}.Configure`, cfg)).Ok) toast('site saved', 'ok');
  });
};
$('siteHere').onclick = () => {
  if (!navigator.geolocation) { toast('this browser cannot tell its location', 'bad'); return; }
  navigator.geolocation.getCurrentPosition(p => {
    const f = $('siteForm').elements;
    f.Lat.value = p.coords.latitude.toFixed(4); f.Lon.value = p.coords.longitude.toFixed(4);
    if (fin(p.coords.altitude)) f.Elev.value = Math.round(p.coords.altitude);
    toast('filled in: check and save');
  }, err => toast('no location: ' + err.message, 'bad'), { enableHighAccuracy: true, timeout: 15000 });
};

function showBacklash(b) {
  $('blPhase').textContent = b.Phase; $('blPhase').className = 'chip ' + phaseClass(b.Phase);
  $('blMessage').textContent = b.Message || '';
  const box = $('blList'); box.textContent = '';
  if (!b.Mounts.length) box.innerHTML = '<p class="hint">Not measured yet.</p>';
  for (const m of b.Mounts) {
    const el = document.createElement('div'); el.className = 'item';
    const arc = (v, sp) => fin(v) ? `${Math.round(v)}″${fin(sp) && sp > 0 ? ` ±${Math.round(sp)}″` : ''}` : '—';
    el.innerHTML = `<div class="row between"><b>${esc(m.MountId)}</b><span class="hint">${esc(utc(m.MeasuredUtc)?.toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' }) ?? '')}</span></div>
      <p class="hint">RA ${arc(m.RaArcsec, m.RaSpreadArcsec)} · Dec ${arc(m.DecArcsec, m.DecSpreadArcsec)}${fin(m.DecDegrees) ? ` · at Dec ${m.DecDegrees.toFixed(0)}°` : ''}</p>`;
    box.append(el);
  }
}
$('blMeasure').onclick = e => busy(e.target, async () => {
  const id = $('blScope').value;
  if (!id) return;
  if (image && ACTIVE.includes(image.Phase)) { toast('not while an image is being taken: it is measured at the start of one', 'bad'); return; }
  toast('measuring: a few moves each way, a few minutes');
  const m = await ask(`${IDS.backlash}.Measure`, id, 20 * 60000);
  if (m?.Ok) toast(`backlash: ${m.Message}`, 'ok'); else toast('not measured: ' + (m?.Message ?? 'no answer'), 'bad');
});

async function loadStation() {
  const [st, solvers] = await Promise.all([ask(`${IDS.storage}.GetState`), ask(IDS.solvers)]);
  stats($('stationStats'), [
    ['Frames saved to', st?.Directory || '—'], ['Saved', st ? `${st.FramesSaved} since the station started` : '—'], ['Last', st?.LastFile?.split(/[\\/]/).pop() || '—'],
    ...(st?.Message ? [['Storage problem', st.Message, 'bad']] : []),
    ['Plate solvers', solvers || 'none', solvers ? '' : 'warn'],
    ['Saving frames of', st?.Watching?.join(', ') || 'nothing', st && st.Watching.length === 0 ? 'warn' : ''],
  ]);
}

// ---------------------------------------------------------------------------------------------------- the log
const logEl = $('log');
const pad = (n, w = 2) => String(n).padStart(w, '0');
function logLine(e) {
  const d = e.TimeUtc ? new Date(e.TimeUtc) : new Date();
  const when = `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
  return `${when} ${String(e.Level).padEnd(5)} ${e.Source || '-'}: ${String(e.Text).replace(/\r?\n/g, ' ⏎ ')}`;
}
function addLog(text, level) {
  const stick = logEl.scrollHeight - logEl.scrollTop - logEl.clientHeight < 40;
  const line = document.createElement('div');
  line.textContent = text;
  if (level) line.className = level;
  logEl.append(line);
  while (logEl.childElementCount > 800) logEl.firstElementChild.remove();
  if (stick) logEl.scrollTop = logEl.scrollHeight;
}
async function loadLog() {
  const tail = await ask(IDS.logTail, 300);
  if (!tail) return;
  $('logFile').textContent = tail.File.split(/[\\/]/).pop();
  logEl.textContent = '';
  for (const text of tail.Text.split('\n').filter(Boolean)) addLog(text, /^\S+ \S+ (warn|error|note)/.exec(text)?.[1]);
  logEl.scrollTop = logEl.scrollHeight;
}
$('logQuiet').onchange = () => logEl.classList.toggle('quiet', $('logQuiet').checked);
$('noteForm').onsubmit = async ev => {
  ev.preventDefault();
  const text = $('note').value.trim();
  if (!text) return;
  $('note').value = '';
  try { await leaf.fire(IDS.logEntry, { TimeUtc: new Date().toISOString(), Source: 'you', Level: 'note', Text: text }); toast('noted', 'ok'); }
  catch (e) { toast('the note did not reach the station: ' + (e.message ?? e), 'bad'); $('note').value = text; }
};

// ---------------------------------------------------------------------------------------------------- start
let started = false, wasDown = false;
leaf.onLink(up => {
  const chip = $('link');
  chip.dataset.state = up ? 'up' : 'down';
  chip.textContent = up ? 'connected' : 'reconnecting…';
  if (!up) { wasDown = true; return; }
  if (started && wasDown) { wasDown = false; setTimeout(() => { reseed(); loadLog().catch(ignore); refreshPicture(); }, 500); }
});
document.addEventListener('visibilitychange', () => { if (!document.hidden && started) { reseed(); if (Date.now() - lastPicture > 60000) refreshPicture(); } });

async function start() {
  const wsPort = await fetch('/api/wsport').then(r => r.json()).catch(() => 8081);
  const url = `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.hostname}:${wsPort}/`;
  for (;;) { try { await leaf.connect(url); break; } catch { $('link').dataset.state = 'down'; $('link').textContent = 'no station at ' + url; await sleep(3000); } }

  showTab(store.get('tab', 'now'));
  await leaf.hook(IDS.logEntry, e => addLog(logLine(e), ['warn', 'error', 'note'].includes(e.Level) ? e.Level : '')).catch(ignore);
  loadLog().catch(ignore);
  follow(`${IDS.imaging}.State`, `${IDS.imaging}.GetState`, showImage).then(paOfRunning);
  follow(`${IDS.scheduler}.State`, `${IDS.scheduler}.GetState`, showSchedState);
  follow(`${IDS.site}.State`, `${IDS.site}.GetState`, showSite);
  follow(`${IDS.backlash}.State`, `${IDS.backlash}.GetState`, showBacklash);
  for (let i = 0; i < 30; i++) {
    try { await leaf.hook(IDS.composeChanged, showComposition); showComposition(await leaf.call(IDS.composeSnapshot, null)); break; } catch { await sleep(2000); }
  }
  for (let i = 0; i < 30; i++) {
    try { await leaf.hook(IDS.frameAdded, e => { flash(e); pictureSoon(); }); break; } catch { await sleep(2000); }
  }
  loadDevices();
  started = true;
  refreshPicture();
  setInterval(() => { if (Date.now() - lastPicture > 120000 && !document.hidden && $('pictureAuto').checked) refreshPicture(); }, 30000);
  setInterval(() => { if (image && !document.hidden) stats($('imageStats'), imageStats(image)); }, 10000);
  // a missed event (a Wi-Fi blip that did not drop the link) must not leave the page showing an old state for long
  setInterval(() => { if (!document.hidden && leaf.connected !== false) reseed(); }, 60000);
}
start();
