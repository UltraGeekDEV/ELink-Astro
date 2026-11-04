// Does what the station's web page (app.js) does, as a leaf from Node: the same IDs, the same calls, no browser needed.
import { Leaf, registerLeaf } from '../../../EVent/js/evn.js';
import { writeFileSync } from 'node:fs';
registerLeaf('bytes', r => { r.take(r.i32()); });
const [,, host, wsPort, step = 'all', out = '.'] = process.argv;
const t = () => new Date().toTimeString().slice(0, 8);
const log = (...a) => console.log(t(), ...a);
const leaf = new Leaf('webtest-' + Math.random().toString(36).slice(2, 6), { keepAlive: { interval: 5000, timeout: 20000 } });
const call = async (id, input = null) => { const t0 = Date.now(); const a = await leaf.call(id, input); return { a, ms: Date.now() - t0 }; };
const t0 = Date.now(); await leaf.connect(`ws://${host}:${wsPort}/`); log(`connected in ${Date.now() - t0} ms`);

const http = await fetch(`http://${host}:8090/`).then(r => r.text()); log(`page: ${http.length} bytes, has app.js: ${http.includes('app.js')}`);
const port = await fetch(`http://${host}:8090/api/wsport`).then(r => r.json()); log(`/api/wsport -> ${port}`);

let { a: img, ms } = await call('ELink.Automation.Imaging.GetState');
let s = img[0]; log(`image state (${ms} ms): ${s.Phase} '${s.Label}' shots ${s.Visits}, depth mean ${s.MeanSeconds.toFixed(1)} min ${s.MinSeconds} of ${s.TargetSeconds}, map ${s.MapCols}x${s.MapRows}`);
let { a: comp } = await call('ELink.Compose.Snapshot'); log(`scopes: ${comp.flatMap(c => c.Scopes).map(x => x.DisplayName || x.Id).join(', ')}`);
let { a: sc } = await call('ELink.Scope.home900.GetState'); log(`scope card: ${sc[0].Phase} '${sc[0].Message}' shot ${sc[0].ShotsDone} of ${sc[0].ShotsPlanned}`);
let { a: tail, ms: ms2 } = await call('ELink.Log.Tail', 200); log(`log tail (${ms2} ms): ${tail[0].File.split('/').pop()}, ${tail[0].Text.split('\n').filter(Boolean).length} lines; last: ${tail[0].Text.trim().split('\n').pop()}`);

const states = [];
await leaf.hook('ELink.Automation.Imaging.State', x => states.push(`${t()} ${x.Phase} shots ${x.Visits}`));
const logs = []; await leaf.hook('ELink.Log.Entry', e => logs.push(`${e.Level} ${e.Source}: ${e.Text}`));

if (step === 'all' || step === 'controls') {
  for (const [name, id] of [['Pause', 'Pause'], ['Resume', 'Resume']]) {
    const { a, ms } = await call(`ELink.Automation.Imaging.${id}`);
    log(`${name} button -> ${JSON.stringify(a.map(x => ({ Ok: x.Ok, Error: x.Error })))} in ${ms} ms`);
    await new Promise(r => setTimeout(r, name === 'Pause' ? 45000 : 2000));    // a whole shot while paused: does a new one start?
    const st = (await call('ELink.Automation.Imaging.GetState')).a[0]; log(`  now ${st.Phase} shots ${st.Visits}`);
  }
  await leaf.fire('ELink.Log.Entry', { TimeUtc: new Date().toISOString(), Source: 'you', Level: 'note', Text: 'web test note: thin cloud from the west' });
  await new Promise(r => setTimeout(r, 1500));
  const tl = (await call('ELink.Log.Tail', 20)).a[0].Text; log(`note in the log file: ${tl.includes('thin cloud from the west')}`);
}
if (step === 'all' || step === 'picture') {
  const settings = { Stretch: true, BackgroundLevel: 0.25, BlackClipSigmas: 2.8, Linked: true, RemoveGradient: true, GradientDegree: 3, GradientDivide: false, NeutralizeBackground: true, Saturation: 1, GreenReduction: 0 };
  const { a, ms } = await call('ELink.Automation.Processing.Process', { Settings: settings, MaxWidth: 1200, MaxHeight: 1200, Source: '', OutOfFocusWeight: 0, PseudoOutput: '' });
  const p = a?.[0]; log(`picture (${ms} ms): ok ${p?.Ok} ${p?.Message ?? ''} ${p?.Ok ? `${p.Width}x${p.Height}, ${p.Frames} frames` : ''}`);
  if (p?.Ok) { const b = p.Png; const v = new DataView(b.buffer, b.byteOffset, b.byteLength); writeFileSync(`${out}/web-picture.png`, b.length >= 4 && v.getInt32(0, true) === b.length - 4 ? b.subarray(4) : b); }
}
log(`state events seen: ${states.length} (${states.slice(-3).join(' | ')})`);
log(`log events seen: ${logs.length}: ${logs.slice(-5).join(' | ')}`);
process.exit(0);
