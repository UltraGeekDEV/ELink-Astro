export default async p => {
  await p.until(`document.getElementById('link').dataset.state === 'up'`, 20000);
  await p.sleep(3000);
  await p.js(`document.querySelector('[data-tab=queue]').click()`); await p.sleep(2500);
  p.log(await p.js(`return document.querySelector('[data-page=queue]').innerText.slice(0, 1500)`));
};
