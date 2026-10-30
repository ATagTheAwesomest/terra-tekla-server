// TERRA server admin page: connected devices (rename, recolour), what each one did recently, and
// every job, load, stack, piece and photo the server keeps, with links between them. It reads the
// same /api routes the iPads use and stays live over the same /ws connection.

const $ = (sel) => document.querySelector(sel);
const KEY = 'terra.admin.key';
const PALETTE = ['#ffb000', '#2f9bff', '#2bd96b', '#ff5c8a', '#b57cff', '#00d1c1', '#ff7a2f', '#e6e14a'];

let devices = [];
let health = null;
const photoUrls = new Map(); // "job/load/name" -> object URL

// ---- Requests -------------------------------------------------------------------------

function key() {
  try { return localStorage.getItem(KEY) ?? ''; } catch { return ''; }
}

async function api(path, { method = 'GET', body } = {}) {
  const res = await fetch(`../api${path}`, {
    method,
    headers: { Authorization: `Bearer ${key()}`, ...(body ? { 'Content-Type': 'application/json' } : {}) },
    body: body ? JSON.stringify(body) : undefined,
    cache: 'no-store',
  });
  if (res.status === 401) {
    askKey('That key was refused.');
    throw new Error('The access key was refused.');
  }
  if (!res.ok) {
    const problem = await res.json().catch(() => null);
    throw new Error(problem?.detail || problem?.title || `Server error ${res.status}`);
  }
  return res.headers.get('content-type')?.includes('json') ? res.json() : res.text();
}

async function photoUrl(job, load, name) {
  const id = `${job}/${load}/${name}`;
  if (photoUrls.has(id)) return photoUrls.get(id);
  const res = await fetch(`../api/photos/${enc(job)}/${enc(load)}/${enc(name)}`, { headers: { Authorization: `Bearer ${key()}` } });
  const url = res.ok ? URL.createObjectURL(await res.blob()) : '';
  photoUrls.set(id, url);
  return url;
}

function askKey(message = '') {
  $('#key-error').textContent = message;
  if (!$('#dlg-key').open) $('#dlg-key').showModal();
}

// ---- Formatting -----------------------------------------------------------------------

const enc = encodeURIComponent;

function esc(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

function when(iso) {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const today = new Date().toDateString() === d.toDateString();
  return today
    ? d.toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit', second: '2-digit' })
    : d.toLocaleString('en-US', { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
}

function ago(iso) {
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (!Number.isFinite(s)) return '';
  if (s < 60) return 'just now';
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86400) return `${Math.round(s / 3600)} h ago`;
  return `${Math.round(s / 86400)} days ago`;
}

const lbs = (n) => `${Math.round(Number(n) || 0).toLocaleString('en-US')} lbs`;

function deviceOf(id) {
  return devices.find((d) => d.id === id);
}

// A device name in its colour, linking to its page.
function who(id, fallback = '') {
  if (!id) return fallback ? `<span class="muted">${esc(fallback)}</span>` : '<span class="muted">PowerFab</span>';
  if (id === 'admin') return '<span class="muted">Admin page</span>';
  const d = deviceOf(id);
  return `<a class="who" style="--c:${esc(d?.color ?? '#777')}" href="#/device/${enc(id)}">${esc(d?.name ?? fallback ?? id)}</a>`;
}

// ---- Truck check (the same matching as the app's js/check.js) --------------------------

function cleanMark(m) {
  return String(m ?? '').trim().toUpperCase().replace(/-+$/, '');
}

function checkStatus(state) {
  const queues = new Map();
  let total = 0;
  for (const st of state.stacks) for (const e of st.items) {
    total++;
    const k = cleanMark(e.mark);
    if (!queues.has(k)) queues.set(k, []);
    queues.get(k).push(e.id);
  }
  const checked = new Map();
  let extras = 0;
  for (const scan of state.truckCheck?.scans ?? []) {
    const id = queues.get(cleanMark(scan.mark))?.shift();
    if (id) checked.set(id, scan);
    else extras++;
  }
  return { total, checked, extras, complete: total > 0 && checked.size === total && !extras };
}

function checkPill(state) {
  if (!state.truckCheck) return '<span class="muted">Not started</span>';
  const s = checkStatus(state);
  if (s.complete) return `<span class="pill ok">Done (${s.total})</span>`;
  return `<span class="pill warn">${s.checked.size} of ${s.total}${s.extras ? `, ${s.extras} extra` : ''}</span>`;
}

function loadTotals(state) {
  let pieces = 0;
  let weight = 0;
  for (const st of state.stacks) for (const e of st.items) {
    pieces++;
    weight += Number(e.weight) || 0;
  }
  return { pieces, weight };
}

// ---- Shared pieces --------------------------------------------------------------------

function feed(items, { empty = 'Nothing yet.', fresh = new Set() } = {}) {
  if (!items.length) return `<p class="muted">${empty}</p>`;
  return `<ul class="feed">${items.map((a) => `
    <li class="${fresh.has(a.id) ? 'fresh' : ''}">
      <time title="${esc(new Date(a.at).toLocaleString())}">${esc(when(a.at))}</time>
      <span class="what"><span class="kind ${esc(a.kind)}">${esc(a.kind.replace('-', ' '))}</span>${who(a.deviceId, a.deviceName)} ${esc(a.text)}</span>
      <span class="where">${[
        a.job ? `<a href="#/job/${enc(a.job)}">Job ${esc(a.job)}</a>` : '',
        a.loadId ? `<a href="#/load/${enc(a.loadId)}">Load ${esc(a.loadNumber ?? '')}</a>` : a.loadNumber ? `<a href="#/job/${enc(a.job ?? '')}">Load ${esc(a.loadNumber)}</a>` : '',
        a.stackName && a.loadId ? `<a href="#/load/${enc(a.loadId)}?stack=${enc(a.stackId ?? '')}">${esc(a.stackName)}</a>` : '',
        a.mark ? `<a href="#/mark/${enc(a.mark)}${a.job ? `?job=${enc(a.job)}` : ''}">${esc(a.mark)}</a>` : '',
      ].filter(Boolean).join('')}</span>
    </li>`).join('')}</ul>`;
}

// A "Show older" button that pages the feed back by id.
function moreButton(items, limit) {
  return items.length >= limit ? `<button class="btn more" type="button" data-more="${items.at(-1).id}">Show older</button>` : '';
}

async function photoGrid(list, { empty = 'No photos.' } = {}) {
  if (!list.length) return `<p class="muted">${empty}</p>`;
  return `<div class="photos">${list.map((p) => `
    <figure class="photo" style="--c:${esc(deviceOf(p.byId ?? p.deviceId)?.color ?? p.color ?? '#555')}" data-photo="${esc(`${p.job}|${p.load ?? p.loadNumber}|${p.name ?? p.fileName}`)}">
      <button type="button" data-photo-open aria-label="View photo"><img alt=""></button>
      <figcaption class="cap">${esc(p.mark || 'Whole load')}
        <span>${who(p.byId ?? p.deviceId, p.by ?? p.deviceName ?? 'unknown')} · ${p.job ? `<a href="#/job/${enc(p.job)}">${esc(p.job)}</a> load ${esc(p.load ?? p.loadNumber)}` : ''}</span>
        <span>${esc(when(p.modified ?? p.takenAt))}</span></figcaption>
    </figure>`).join('')}</div>`;
}

// Fills the pictures in after the grid is drawn.
function fillPhotos() {
  for (const fig of document.querySelectorAll('[data-photo]')) {
    const [job, load, name] = fig.dataset.photo.split('|');
    photoUrl(job, load, name).then((url) => {
      const img = fig.querySelector('img');
      if (img && url) img.src = url;
    });
  }
}

// ---- Pages ----------------------------------------------------------------------------

let freshIds = new Set();

async function pageHome() {
  const [items] = await Promise.all([api('/activity?limit=80'), refreshHealth()]);
  const online = devices.filter((d) => d.online).length;
  return `<div class="grid">
    <div>
      <section class="panel">
        <h2>Devices <span class="muted">· ${online} connected</span></h2>
        <div class="devices">${devices.length ? devices.map(deviceCard).join('') : '<p class="muted">No iPad has connected yet. Open the app on one: it shows up here.</p>'}</div>
      </section>
      <section class="panel">
        <h2>Server</h2>
        ${healthList()}
      </section>
    </div>
    <section class="panel">
      <h2>Recent activity</h2>
      <div data-feed>${feed(items, { fresh: freshIds })}</div>
      ${moreButton(items, 80)}
    </section>
  </div>`;
}

function deviceCard(d) {
  return `<a class="device${d.online ? ' online' : ''}" style="--c:${esc(d.color)}" href="#/device/${enc(d.id)}">
    <span class="dot" title="${d.online ? 'Connected' : 'Not connected'}"></span>
    <span class="device-name">${esc(d.name)}</span>
    <span class="muted">${d.online ? 'Connected' : `Seen ${esc(ago(d.lastSeenAt))}`}</span>
    <span class="device-meta">${d.appVersion ? `App v${esc(d.appVersion)} · ` : ''}${esc(d.lastIp ?? '')}</span>
  </a>`;
}

async function refreshHealth() {
  try { health = await api('/status'); } catch { health = null; }
}

function healthList() {
  if (!health) return '<p class="error">Couldn’t read /api/status.</p>';
  const c = health.conversion;
  return `<dl class="health">
    <dt>Server</dt><dd>v${esc(health.version)}${health.accessKeySet ? '' : ' · <span class="error">ACCESS_KEY not set: anyone on the network can use it</span>'}</dd>
    <dt>iPad app</dt><dd>${health.client.served ? `v${esc(health.client.version ?? '?')} from <code>${esc(health.client.root)}</code>` : `<span class="error">Not served: CLIENT_ROOT (${esc(health.client.root || 'empty')}) has no index.html</span>`}</dd>
    <dt>PowerFab bridge</dt><dd>${c?.ok ? `Reachable · ${esc(c.health?.mode ?? '')} mode · writes ${c.health?.writesEnabled ? 'on' : '<span class="error">off</span>'}` : `<span class="error">${esc(c?.error ?? 'Unreachable')}</span>`}</dd>
    <dt>Photos</dt><dd>${health.photosEnabled ? 'On' : 'Off (PHOTOS_ENABLED)'}</dd>
    <dt>Database</dt><dd><code>${esc(health.database)}</code></dd>
  </dl>`;
}

async function pageDevice(id) {
  const d = deviceOf(id);
  if (!d) return '<p class="error">No such device.</p>';
  const [items, photos] = await Promise.all([
    api(`/activity?device=${enc(id)}&limit=100`),
    health?.photosEnabled ? api(`/photos/recent?device=${enc(id)}&limit=24`).catch(() => []) : [],
  ]);
  const swatches = [...new Set([...PALETTE, d.color])].map((c) => `<button type="button" class="swatch${c === d.color ? ' on' : ''}" style="--c:${c}" data-color="${c}" aria-label="Colour ${c}"></button>`).join('');
  return `<p class="crumbs"><a href="#/">Devices</a> › ${esc(d.name)}</p>
  <div class="grid">
    <div>
      <section class="panel" style="border-left:6px solid ${esc(d.color)}">
        <h1>${esc(d.name)}</h1>
        <p class="muted">${d.online ? 'Connected now' : `Last seen ${esc(ago(d.lastSeenAt))}`} · first seen ${esc(when(d.createdAt))}${d.appVersion ? ` · app v${esc(d.appVersion)}` : ''}</p>
        <form class="edit" id="form-device" data-id="${esc(id)}">
          <label for="device-name">Name (shown on every iPad and on their scans and photos)</label>
          <input id="device-name" type="text" maxlength="40" value="${esc(d.name)}">
          <button class="btn primary" type="submit">Save</button>
          <label>Colour</label>
          <div class="swatches">${swatches}<input type="color" value="${esc(d.color)}" data-color-pick aria-label="Any colour"></div>
        </form>
        <p class="muted" id="device-saved"></p>
        <p class="muted">${esc(d.userAgent ?? '')}</p>
      </section>
      ${photos.length ? `<section class="panel"><h2>Recent photos</h2>${await photoGrid(photos)}</section>` : ''}
    </div>
    <section class="panel">
      <h2>What ${esc(d.name)} did recently</h2>
      <div data-feed>${feed(items, { empty: 'Nothing yet.', fresh: freshIds })}</div>
      ${moreButton(items, 100)}
    </section>
  </div>`;
}

async function pageJobs() {
  const jobs = await api('/jobs');
  return `<section class="panel"><h2>Jobs the server has stacks for</h2>
    ${jobs.length ? `<table><thead><tr><th>Job</th><th class="num">Loads</th><th class="num">Pieces in stacks</th><th>Last activity</th></tr></thead><tbody>
    ${jobs.map((j) => `<tr><td><a href="#/job/${enc(j.job)}"><b>${esc(j.job)}</b></a></td><td class="num">${j.loads}</td><td class="num">${j.pieces}</td><td>${j.lastAt ? esc(ago(j.lastAt)) : ''}</td></tr>`).join('')}
    </tbody></table>` : '<p class="muted">None yet. Loads show up here once an iPad opens them in Shipping.</p>'}</section>`;
}

async function pageJob(job) {
  const [loads, items] = await Promise.all([api(`/jobs/${enc(job)}/loads`), api(`/activity?job=${enc(job)}&limit=100`)]);
  return `<p class="crumbs"><a href="#/jobs">Jobs</a> › ${esc(job)}</p>
  <div class="grid">
    <section class="panel"><h1>Job ${esc(job)}</h1>
      <p class="muted">Loads the iPads have opened. PowerFab itself has the full list.</p>
      ${loads.length ? `<table><thead><tr><th>Load</th><th class="num">Stacks</th><th class="num">Pieces</th><th class="num">Weight</th><th>Truck check</th><th>Changed</th></tr></thead><tbody>
      ${loads.map((l) => {
        const t = loadTotals(l);
        return `<tr><td><a href="#/load/${enc(l.loadId)}"><b>Load ${esc(l.loadNumber)}</b></a></td><td class="num">${l.stacks.length}</td><td class="num">${t.pieces}</td><td class="num">${lbs(t.weight)}</td><td>${checkPill(l)}</td><td>${esc(ago(l.updatedAt))}</td></tr>`;
      }).join('')}</tbody></table>` : '<p class="muted">No loads yet.</p>'}
    </section>
    <section class="panel"><h2>Activity on ${esc(job)}</h2><div data-feed>${feed(items, { fresh: freshIds })}</div>${moreButton(items, 100)}</section>
  </div>`;
}

async function pageLoad(loadId, query) {
  const state = await api(`/loads/${enc(loadId)}`);
  const [items, photoList] = await Promise.all([
    api(`/activity?load=${enc(loadId)}&limit=100`),
    health?.photosEnabled && state.job ? api(`/photos/${enc(state.job)}/${enc(state.loadNumber)}`).then((r) => r.files.map((f) => ({ ...f, job: state.job, load: state.loadNumber }))).catch(() => []) : [],
  ]);
  const t = loadTotals(state);
  const check = checkStatus(state);
  const focus = query.get('stack');
  const stacks = state.stacks.map((st) => {
    const w = st.items.reduce((n, e) => n + (Number(e.weight) || 0), 0);
    return `<section class="stack" id="stack-${esc(st.id)}"${st.id === focus ? ' style="outline:2px solid var(--accent)"' : ''}>
      <div class="stack-head"><b>${esc(st.name)}</b><span class="muted">${st.items.length} pcs · ${lbs(w)}</span>${st.createdBy ? `<span class="muted">made by ${who(st.createdBy)}</span>` : ''}</div>
      ${st.items.length ? `<table><thead><tr><th>#</th><th>Mark</th><th>Seq</th><th>Description</th><th class="num">Weight</th><th>Scanned by</th><th>When</th>${state.truckCheck ? '<th>Checked</th>' : ''}</tr></thead><tbody>
      ${st.items.map((e, i) => {
        const scan = check.checked.get(e.id);
        return `<tr class="${e.pending ? 'pending' : ''}${e.manual ? ' manual' : ''}">
          <td>${i + 1}</td><td class="mark"><a href="#/mark/${enc(e.mark)}?job=${enc(state.job)}">${esc(e.mark)}</a></td><td>${esc(e.sequence)}</td>
          <td>${e.pending ? 'Waiting on PowerFab…' : e.removing ? 'Being taken off…' : esc(e.description ?? '')}${e.manual ? ' <span class="pill warn">typed</span>' : ''}</td>
          <td class="num">${lbs(e.weight)}</td><td>${who(e.scannedBy)}</td><td>${e.scannedAt ? esc(when(e.scannedAt)) : '<span class="muted">loaded in PowerFab</span>'}</td>
          ${state.truckCheck ? `<td>${scan ? `✓ ${who(scan.scannedBy, 'solo')}` : ''}</td>` : ''}</tr>`;
      }).join('')}</tbody></table>` : '<p class="muted" style="padding:.5rem .85rem">Empty.</p>'}
    </section>`;
  }).join('');
  return `<p class="crumbs"><a href="#/jobs">Jobs</a> › <a href="#/job/${enc(state.job)}">${esc(state.job)}</a> › Load ${esc(state.loadNumber)}</p>
  <div class="grid wide-left">
    <div>
      <section class="panel">
        <h1>Load ${esc(state.loadNumber)} <span class="muted">· ${esc(state.job)}</span></h1>
        <div class="stats">
          <div class="stat"><b>${t.pieces}</b><span>pieces</span></div>
          <div class="stat"><b>${lbs(t.weight)}</b><span>weight</span></div>
          <div class="stat"><b>${state.stacks.length}</b><span>stacks</span></div>
          <div class="stat"><b>${checkPill(state)}</b><span>shared truck check</span></div>
        </div>
        ${stacks}
      </section>
      ${health?.photosEnabled ? `<section class="panel"><h2>Photos</h2>${await photoGrid(photoList.slice().reverse(), { empty: 'No photos of this load.' })}</section>` : ''}
    </div>
    <section class="panel"><h2>Activity on this load</h2><div data-feed>${feed(items, { fresh: freshIds })}</div>${moreButton(items, 100)}</section>
  </div>`;
}

async function pageMark(mark, query) {
  const job = query.get('job') ?? '';
  const items = await api(`/activity?mark=${enc(mark)}${job ? `&job=${enc(job)}` : ''}&limit=100`);
  return `<p class="crumbs">${job ? `<a href="#/job/${enc(job)}">${esc(job)}</a> › ` : ''}Piece ${esc(mark)}</p>
    <section class="panel"><h1 class="mark">${esc(mark)}</h1><h2>Everything done with this mark</h2><div data-feed>${feed(items, { fresh: freshIds })}</div>${moreButton(items, 100)}</section>`;
}

async function pagePhotos() {
  if (!health) await refreshHealth();
  if (!health?.photosEnabled) return '<section class="panel"><h2>Photos</h2><p class="muted">Photos are off on this server (PHOTOS_ENABLED). See Docs › Photos.</p></section>';
  const list = await api('/photos/recent?limit=60');
  return `<section class="panel"><h2>Recent photos</h2>${await photoGrid(list)}</section>`;
}

// A small Markdown reader for the docs: headings, paragraphs, lists, tables, code.
function markdown(md) {
  const inline = (s) => esc(s)
    .replace(/`([^`]+)`/g, '<code>$1</code>')
    .replace(/\*\*([^*]+)\*\*/g, '<b>$1</b>')
    .replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (m, text, href) => `<a href="${/^https?:/.test(href) ? href : '#/docs'}" target="_blank" rel="noopener">${text}</a>`);
  const out = [];
  const lines = md.replace(/\r/g, '').split('\n');
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (line.startsWith('```')) {
      const code = [];
      for (i++; i < lines.length && !lines[i].startsWith('```'); i++) code.push(lines[i]);
      out.push(`<pre><code>${esc(code.join('\n'))}</code></pre>`);
    } else if (/^#{1,4} /.test(line)) {
      const n = line.match(/^#+/)[0].length;
      out.push(`<h${n}>${inline(line.slice(n + 1))}</h${n}>`);
    } else if (line.startsWith('|')) {
      const rows = [];
      for (; i < lines.length && lines[i].startsWith('|'); i++) rows.push(lines[i]);
      i--;
      const cells = (r) => r.slice(1, r.endsWith('|') ? -1 : undefined).split(/(?<!\\)\|/).map((c) => inline(c.trim().replace(/\\\|/g, '|')));
      const body = rows.filter((r, k) => k !== 1);
      out.push(`<table><thead><tr>${cells(body[0]).map((c) => `<th>${c}</th>`).join('')}</tr></thead><tbody>${body.slice(1).map((r) => `<tr>${cells(r).map((c) => `<td>${c}</td>`).join('')}</tr>`).join('')}</tbody></table>`);
    } else if (/^\s*([-*]|\d+\.) /.test(line)) {
      const ordered = /^\s*\d+\./.test(line);
      const items = [];
      for (; i < lines.length && /^\s*([-*]|\d+\.) /.test(lines[i]); i++) {
        let item = lines[i].replace(/^\s*([-*]|\d+\.) /, '');
        while (i + 1 < lines.length && /^ {2,}\S/.test(lines[i + 1])) item += ` ${lines[++i].trim()}`;
        items.push(`<li>${inline(item)}</li>`);
      }
      i--;
      out.push(ordered ? `<ol>${items.join('')}</ol>` : `<ul>${items.join('')}</ul>`);
    } else if (line.trim()) {
      const para = [line];
      while (i + 1 < lines.length && lines[i + 1].trim() && !/^(#|```|\||\s*([-*]|\d+\.) )/.test(lines[i + 1])) para.push(lines[++i]);
      out.push(`<p>${inline(para.join(' '))}</p>`);
    }
  }
  return out.join('\n');
}

async function pageDocs(name = 'setup') {
  const text = await api(`/docs/${enc(name)}`);
  return `<div class="tabs"><a href="#/docs/setup" class="${name === 'setup' ? 'active' : ''}">Setup &amp; reference</a><a href="#/docs/photos" class="${name === 'photos' ? 'active' : ''}">Photos</a></div>
    <article class="panel doc">${markdown(text)}</article>`;
}

// ---- Router ---------------------------------------------------------------------------

let drawing = 0;

async function draw() {
  const seq = ++drawing;
  const [path, qs] = location.hash.replace(/^#\/?/, '').split('?');
  const [page, ...rest] = path.split('/');
  const arg = decodeURIComponent(rest.join('/'));
  const query = new URLSearchParams(qs ?? '');
  document.querySelectorAll('[data-nav]').forEach((a) => a.classList.toggle('active',
    a.dataset.nav === (page === '' || page === 'device' ? 'home' : ['job', 'load', 'mark'].includes(page) ? 'jobs' : page)));
  try {
    const html = page === 'device' ? await pageDevice(arg)
      : page === 'jobs' ? await pageJobs()
      : page === 'job' ? await pageJob(arg)
      : page === 'load' ? await pageLoad(arg, query)
      : page === 'mark' ? await pageMark(arg, query)
      : page === 'photos' ? await pagePhotos()
      : page === 'docs' ? await pageDocs(arg || 'setup')
      : await pageHome();
    if (seq !== drawing) return;
    const scroll = window.scrollY;
    $('#main').innerHTML = html;
    window.scrollTo(0, scroll);
    fillPhotos();
    const focus = query.get('stack');
    if (focus && redrawReason === 'nav') document.getElementById(`stack-${focus}`)?.scrollIntoView({ block: 'start' });
  } catch (err) {
    if (seq === drawing) $('#main').innerHTML = `<p class="error">${esc(err.message)}</p>`;
  }
  redrawReason = 'nav';
  freshIds = new Set();
}

let redrawReason = 'nav';
let redrawTimer = null;

// Live changes redraw the page, at most a few times a second.
function redrawSoon() {
  if (document.querySelector('input:focus') || $('#dlg-photo').open) return;
  clearTimeout(redrawTimer);
  redrawTimer = setTimeout(() => { redrawReason = 'live'; draw(); }, 250);
}

// ---- Live connection --------------------------------------------------------------------

let socket = null;

function connect() {
  const url = new URL('../ws', location.href);
  url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
  socket = new WebSocket(url);
  socket.onopen = () => socket.send(JSON.stringify({ type: 'hello', key: key(), kind: 'admin' }));
  socket.onmessage = (e) => {
    const msg = JSON.parse(e.data);
    if (msg.type === 'welcome' || msg.type === 'devices') {
      devices = msg.devices;
      $('#live').textContent = 'Live';
      $('#live').classList.add('on');
      redrawSoon();
    } else if (msg.type === 'activity') {
      for (const a of msg.items) freshIds.add(a.id);
      redrawSoon();
    } else if (msg.type === 'load' || msg.type === 'photos') {
      redrawSoon();
    }
  };
  socket.onclose = (e) => {
    $('#live').textContent = 'Reconnecting…';
    $('#live').classList.remove('on');
    if (e.code === 4401) askKey('That key was refused.');
    else setTimeout(connect, 3000);
  };
}

// ---- Wiring ---------------------------------------------------------------------------

async function saveDevice(form, changes) {
  try {
    await api(`/devices/${enc(form.dataset.id)}`, { method: 'PUT', body: changes });
    devices = await api('/devices');
    $('#device-saved').textContent = 'Saved. Every iPad shows the change now.';
    draw();
  } catch (err) {
    $('#device-saved').textContent = err.message;
  }
}

function bind() {
  window.addEventListener('hashchange', () => { redrawReason = 'nav'; draw(); });
  $('#form-key').addEventListener('submit', () => {
    try { localStorage.setItem(KEY, $('#input-key').value.trim()); } catch { /* private window */ }
    socket?.close();
    start();
  });
  $('#main').addEventListener('submit', (e) => {
    if (e.target.id !== 'form-device') return;
    e.preventDefault();
    saveDevice(e.target, { name: $('#device-name').value });
  });
  $('#main').addEventListener('click', async (e) => {
    const sw = e.target.closest('[data-color]');
    if (sw) saveDevice($('#form-device'), { color: sw.dataset.color });
    const more = e.target.closest('[data-more]');
    if (more) {
      const [path, qs] = location.hash.replace(/^#\/?/, '').split('?');
      const [page, ...rest] = path.split('/');
      const arg = decodeURIComponent(rest.join('/'));
      const filter = page === 'device' ? `device=${enc(arg)}` : page === 'job' ? `job=${enc(arg)}` : page === 'load' ? `load=${enc(arg)}`
        : page === 'mark' ? `mark=${enc(arg)}${new URLSearchParams(qs ?? '').get('job') ? `&job=${enc(new URLSearchParams(qs).get('job'))}` : ''}` : '';
      const older = await api(`/activity?${filter}&before=${more.dataset.more}&limit=100`);
      more.insertAdjacentHTML('beforebegin', feed(older, { empty: 'Nothing older.' }));
      more.remove();
      if (older.length >= 100) document.querySelector('[data-feed]').parentElement.insertAdjacentHTML('beforeend', moreButton(older, 100));
    }
    const open = e.target.closest('[data-photo-open]');
    if (open) {
      const [job, load, name] = open.closest('[data-photo]').dataset.photo.split('|');
      $('#photo-title').textContent = `${job} · load ${load} · ${name}`;
      $('#photo-img').src = await photoUrl(job, load, name);
      $('#dlg-photo').showModal();
    }
  });
  $('#main').addEventListener('change', (e) => {
    if (e.target.matches('[data-color-pick]')) saveDevice($('#form-device'), { color: e.target.value });
  });
  $('#btn-photo-close').addEventListener('click', () => $('#dlg-photo').close());
  $('#dlg-photo').addEventListener('click', (e) => { if (e.target === e.currentTarget) e.currentTarget.close(); });
}

async function start() {
  try {
    devices = await api('/devices');
  } catch {
    return; // askKey() is showing
  }
  connect();
  draw();
}

bind();
start();
