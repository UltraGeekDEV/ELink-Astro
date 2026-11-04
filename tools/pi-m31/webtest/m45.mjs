import { Leaf, registerLeaf } from '../../../EVent/js/evn.js';
registerLeaf('bytes', r => { r.take(r.i32()); });
const leaf = new Leaf('m45-' + Math.random().toString(36).slice(2, 6));
await leaf.connect(`ws://${process.env.PI ?? "192.168.137.48"}:8091/`);
const saved = (await leaf.call('ELink.Automation.Imaging.ListSaved', null))[0];
const r = saved.Images.find(i => i.Label.includes('45')).Request; r.Resume = true;
const t = Date.now();
console.log(JSON.stringify(await leaf.call('ELink.Automation.Imaging.Start', r, { timeout: 300000 })), (Date.now() - t) / 1000, 's', leaf.lastProblem ?? '');
process.exit(0);
