// A page that freezes: joins like app.js, hooks what it hooks, then the test stops the process (SIGSTOP) with the socket open.
import { Leaf, registerLeaf } from '../../../EVent/js/evn.js';
registerLeaf('bytes', r => { r.take(r.i32()); });
const leaf = new Leaf('frozen-' + Math.random().toString(36).slice(2, 6), { keepAlive: { interval: 5000, timeout: 20000 } });
await leaf.connect(`ws://${process.argv[2]}:8091/`);
for (const id of ['ELink.Automation.Imaging.State', 'ELink.Log.Entry', 'ELink.Automation.LiveStack.FrameAdded', 'ELink.Scope.home900.State'])
  await leaf.hook(id, () => { }).catch(e => console.log('hook', id, e?.message));
console.log('ready', process.pid);
setInterval(() => { }, 1 << 30);
