export default async p => {
  await p.until(`document.getElementById('link').dataset.state === 'up'`, 30000);
  await p.sleep(5000);
  await p.js(`document.querySelector('[data-tab=queue]').click()`); await p.sleep(2000);
  await p.js(`[...document.querySelectorAll('[data-page=queue] button')].find(b => b.textContent.trim() === 'Stop the queue').click()`); await p.sleep(3000);
  p.log('queue:', await p.js(`return document.querySelector('[data-page=queue]').innerText.slice(0, 120).replace(/\\n+/g, ' / ')`));
  await p.js(`document.querySelector('[data-tab=kept]').click()`); await p.sleep(3000);
  p.log('kept:', await p.js(`return keptList.innerText.replace(/\\n+/g, ' / ')`));
  await p.js(`[...keptList.querySelectorAll('.item')].find(i => i.innerText.includes('M31 wide')).querySelector('[data-a=go]').click()`);
  await p.until(`imagePhase.textContent === 'Running'`, 300000);
  p.log('continued:', await p.js(`return imageLabel.textContent + ' | ' + imagePhase.textContent + ' | ' + imageMessage.textContent + ' | ' + imageStats.innerText.replace(/\\n/g, ' ')`));
};
