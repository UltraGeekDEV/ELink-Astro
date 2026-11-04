// The buttons, as a person would press them: pause, resume, queue add/remove, stop, continue from Kept.
export default async p => {
  await p.until(`document.getElementById('link').dataset.state === 'up'`, 20000);
  await p.js(`window.confirm = () => true; document.querySelector('[data-tab=now]').click()`);
  await p.sleep(3000);
  const st = () => p.js(`return imagePhase.textContent + ' | ' + imageMessage.textContent + ' | toasts: ' + toasts.innerText.replace(/\\n/g, ' / ')`);
  p.log('start:', await st());
  await p.js(`pause.click()`); await p.until(`imagePhase.textContent === 'Paused'`, 60000); p.log('after Pause:', await st());
  p.log('resume visible:', await p.js(`return !resume.hidden && pause.hidden`));
  await p.js(`resume.click()`); await p.until(`imagePhase.textContent === 'Running'`, 20000); p.log('after Resume:', await st());
  // queue: add the plan, see it, remove it, save
  await p.js(`document.querySelector('[data-tab=plan]').click()`); await p.sleep(1500);
  await p.js(`const f = document.getElementById('find'); f.value = 'M 33'; f.dispatchEvent(new Event('input'))`); await p.sleep(300);
  await p.js(`found.firstElementChild.click()`); await p.sleep(500);
  await p.js(`planForm.elements.Label.value = 'web test queue'; planForm.elements.Label.dispatchEvent(new Event('input', { bubbles: true }))`); await p.sleep(800);
  await p.js(`addQueue.click()`); await p.sleep(3000); p.log('add to queue:', await st());
  await p.js(`document.querySelector('[data-tab=queue]').click()`); await p.sleep(3000);
  p.log('queue:', await p.js(`return queueList.innerText.replace(/\\n+/g, ' / ') + ' || ' + schedMessage.textContent + ' ' + schedPhase.textContent`));
  await p.shot('6-queue');
  await p.js(`[...queueList.querySelectorAll('.item')].find(i => i.innerText.includes('web test queue')).querySelector('[data-a=del]').click(); queueSave.click()`);
  await p.sleep(3000); p.log('after remove+save:', await p.js(`return queueList.innerText.replace(/\\n+/g, ' / ')`));
  // stop and carry on from Kept
  await p.js(`document.querySelector('[data-tab=now]').click()`); await p.sleep(500);
  await p.js(`document.getElementById('stop').click()`); await p.until(`imagePhase.textContent === 'Aborted' || imagePhase.textContent === 'Idle'`, 90000); p.log('after Stop:', await st());
  await p.js(`document.querySelector('[data-tab=kept]').click()`); await p.sleep(3000);
  p.log('kept:', await p.js(`return keptList.innerText.replace(/\\n+/g, ' / ')`));
  await p.shot('7-kept');
  await p.js(`[...keptList.querySelectorAll('.item')].find(i => i.innerText.includes('M31 wide')).querySelector('[data-a=go]').click()`);
  const t = Date.now();
  await p.until(`imagePhase.textContent === 'Running'`, 300000); p.log(`after Continue (${((Date.now() - t) / 1000).toFixed(0)} s):`, await st());
  await p.sleep(40000);
  p.log('a shot later:', await p.js(`return imageStats.innerText.replace(/\\n/g, ' ')`));
  await p.shot('8-now-continued');
};
