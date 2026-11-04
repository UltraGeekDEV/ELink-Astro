export default async p => {
  await p.until(`document.getElementById('link').dataset.state === 'up'`, 30000);
  await p.sleep(6000);
  p.log(await p.js(`return imageLabel.textContent + ' | ' + imagePhase.textContent + ' | ' + imageStats.innerText.replace(/\\n/g, ' ')`));
  await p.shot('now-soak');
};
