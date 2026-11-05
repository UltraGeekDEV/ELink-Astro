import { Leaf } from '../../../EVent/js/evn.js';
const leaf = new Leaf('sch-' + Math.random().toString(36).slice(2, 6));
await leaf.connect(`ws://${process.env.PI ?? "192.168.137.48"}:8091/`);
console.log(JSON.stringify((await leaf.call('ELink.Automation.Scheduler.GetState', null))?.[0]));
const s = (await leaf.call('ELink.Automation.Scheduler.GetSchedule', null))?.[0];
console.log(JSON.stringify(s.Entries.map(e => ({ l: e.Request.Label, en: e.Enabled, alt: e.MinAltitudeDegrees, dark: e.RequireDark, moon: e.MaxMoonIllumination, sep: e.MinMoonSeparationDegrees, nb: e.NotBeforeUtc, na: e.NotAfterUtc, scopes: e.Request.ScopeIds, target: e.Request.TargetSeconds }))));
process.exit(0);
