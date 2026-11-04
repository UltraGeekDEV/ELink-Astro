// A tour of every page: looks at what each shows, plans M 31, screenshots; starts nothing.
export default async p => {
  const ok = await p.until(`document.getElementById('link').dataset.state === 'up'`, 20000);
  p.log('connected:', ok);
  await p.sleep(4000);
  p.log('now:', await p.js(`return { label: imageLabel.textContent, phase: imagePhase.textContent, stats: imageStats.innerText, sky: sky.textContent, cover: !cover.parentElement.hidden, scopes: document.querySelectorAll('#scopes .scope').length }`));
  await p.shot('1-now');
  for (const tab of ['kept', 'queue', 'rig', 'log']) {
    await p.js(`document.querySelector('[data-tab=${tab}]').click()`);
    await p.sleep(3500);
    p.log(tab + ':', await p.js(`return document.querySelector('[data-page=${tab}]').innerText.slice(0, 700)`));
    await p.shot('2-' + tab);
  }
  await p.js(`document.querySelector('[data-tab=plan]').click()`);
  await p.sleep(500);
  await p.js(`resetForm.click(); const f = document.getElementById('find'); f.value = 'andromeda'; f.dispatchEvent(new Event('input'))`);
  await p.sleep(300);
  p.log('search:', await p.js(`return found.innerText`));
  await p.js(`found.firstElementChild.click()`);
  await p.js(`lensMm.value = 150; lensApply.click(); planForm.elements.Exposure.value = 30; planForm.elements.Depth.value = 10; mp.value = 8; mp.dispatchEvent(new Event('input'))`);
  await p.sleep(3000);
  p.log('plan:', await p.js(`return { label: planForm.elements.Label.value, ra: planForm.elements.Ra.value, dec: planForm.elements.Dec.value, w: planForm.elements.Width.value, h: planForm.elements.Height.value, pa: planForm.elements.Pa.value, scale: planForm.elements.Scale.value, out: outSize.textContent, info: planInfo.textContent, obs: obs.textContent }`));
  await p.js(`matchCamera.click()`);
  await p.sleep(3000);
  p.log('after match camera:', await p.js(`return { pa: planForm.elements.Pa.value, info: planInfo.textContent, toast: toasts.innerText }`));
  await p.shot('3-plan');
  await p.viewport(390, 844);
  await p.js(`document.querySelector('[data-tab=now]').click()`);
  await p.sleep(1500);
  await p.shot('4-phone-now', false);
  await p.js(`document.querySelector('[data-tab=plan]').click()`);
  await p.sleep(1500);
  await p.shot('5-phone-plan');
  p.log('overflow:', await p.js(`return document.documentElement.scrollWidth > innerWidth ? 'page wider than the phone: ' + document.documentElement.scrollWidth : 'none'`));
};
