export default async p => {
  await p.until(`document.getElementById('link').dataset.state === 'up'`, 30000);
  await p.sleep(4000);
  p.log('after restart:', await p.js(`return imageLabel.textContent + ' | ' + imagePhase.textContent + ' | ' + imageMessage.textContent`));
  await p.js(`document.querySelector('[data-tab=kept]').click()`); await p.sleep(3000);
  p.log('kept:', await p.js(`return keptList.innerText.replace(/\\n+/g, ' / ')`));
  await p.js(`[...keptList.querySelectorAll('.item')].find(i => i.innerText.includes('M31 wide')).querySelector('[data-a=go]').click()`);
  await p.until(`imagePhase.textContent === 'Running'`, 300000);
  p.log('continued:', await p.js(`return imagePhase.textContent + ' | ' + imageMessage.textContent + ' | ' + toasts.innerText.replace(/\\n/g, ' / ')`));
};
