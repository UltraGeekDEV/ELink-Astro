// close headless test Firefoxes by their BiDi ports: node ffclose.mjs PORT...
for (const port of process.argv.slice(2)) {
  try {
    const ws = new WebSocket(`ws://127.0.0.1:${port}/session`);
    await new Promise((ok, bad) => { ws.onopen = ok; ws.onerror = bad; setTimeout(bad, 5000); });
    let id = 0; const send = (method, params = {}) => new Promise(ok => { const my = ++id; ws.addEventListener('message', m => { const x = JSON.parse(m.data); if (x.id === my) ok(x); }); ws.send(JSON.stringify({ id: my, method, params })); setTimeout(() => ok({ timeout: true }), 5000); });
    const s = await send('session.new', { capabilities: {} });
    const c = await send('browser.close');
    console.log(port, s.type ?? '', c.type ?? c.timeout ?? 'sent');
  } catch (e) { console.log(port, 'no answer', e?.message ?? ''); }
}
process.exit(0);
