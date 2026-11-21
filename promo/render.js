// Renders promo.html to JPEG frames with headless Chrome (no npm packages needed; Node 22+).
// Usage: node render.js <outDir> [fps=30] [onlyTimes e.g. "1.5,6.8"]
const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');
const os = require('os');

const outDir = process.argv[2];
const fps = Number(process.argv[3] || 30);
const only = process.argv[4] ? process.argv[4].split(',').map(Number) : null;
const CHROME = process.env.CHROME || 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const PORT = 9333;
const page = 'file:///' + path.resolve(__dirname, 'promo.html').replace(/\\/g, '/');

const sleep = ms => new Promise(r => setTimeout(r, ms));

(async () => {
  fs.mkdirSync(outDir, { recursive: true });
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'promo-chrome-'));
  const chrome = spawn(CHROME, ['--headless=new', `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`,
    '--hide-scrollbars', '--force-color-profile=srgb', '--window-size=1280,720', 'about:blank'], { stdio: 'ignore' });

  let target;
  for (let i = 0; i < 50 && !target; i++) {
    try { target = (await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json()).find(t => t.type === 'page'); }
    catch { await sleep(200); }
  }
  if (!target) throw new Error('Chrome did not start');

  const ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise(r => ws.addEventListener('open', r, { once: true }));
  let id = 0; const pending = new Map();
  ws.addEventListener('message', e => {
    const m = JSON.parse(e.data);
    if (m.id && pending.has(m.id)) { const { res, rej } = pending.get(m.id); pending.delete(m.id); m.error ? rej(new Error(m.error.message)) : res(m.result); }
  });
  const send = (method, params = {}) => new Promise((res, rej) => { const i = ++id; pending.set(i, { res, rej }); ws.send(JSON.stringify({ id: i, method, params })); });

  await send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 720, deviceScaleFactor: 1.5, mobile: false });
  await send('Page.navigate', { url: page });
  for (let i = 0; i < 50; i++) {
    const r = await send('Runtime.evaluate', { expression: 'document.readyState === "complete" && typeof render === "function"', returnByValue: true });
    if (r.result.value) break;
    await sleep(100);
  }
  await send('Runtime.evaluate', { expression: 'document.fonts.ready', awaitPromise: true });
  const duration = (await send('Runtime.evaluate', { expression: 'DURATION', returnByValue: true })).result.value;

  const times = only ?? Array.from({ length: Math.round(duration * fps) }, (_, f) => f / fps);
  for (let f = 0; f < times.length; f++) {
    await send('Runtime.evaluate', { expression: `render(${times[f]})` });
    const shot = await send('Page.captureScreenshot', { format: 'jpeg', quality: 94 });
    const name = only ? `t${times[f]}.jpg` : `f${String(f).padStart(4, '0')}.jpg`;
    fs.writeFileSync(path.join(outDir, name), Buffer.from(shot.data, 'base64'));
    if (f % 90 === 0) console.log(`frame ${f}/${times.length}`);
  }

  ws.close();
  chrome.kill();
  console.log(`done: ${times.length} frames`);
})().catch(e => { console.error(e); process.exit(1); });
