// TERRA server admin page: connected devices (rename, recolour), what each one did recently, and
// every job, load, stack, piece and photo the server keeps, with links between them. It reads the
// same /api routes the iPads use and stays live over the same /ws connection.
//
// The whole board sits behind the access key (the same one the iPads use) and the admin code,
// entered on an on-screen keypad. The server checks the code and every /api/admin route itself;
// this page keeps the token it gets in localStorage, shared with the iPad app (same server), and
// sends it as X-Terra-Admin. The token is good for 30 minutes from the IP address it was got from.
// It installs as an app (manifest.webmanifest, sw.js).

const $ = (sel) => document.querySelector(sel);
const KEY = 'terra.admin.key';
const UNLOCK = 'terra.admin.unlock';
const PALETTE = ['#ffb000', '#2f9bff', '#2bd96b', '#ff5c8a', '#b57cff', '#00d1c1', '#ff7a2f', '#e6e14a'];

let devices = [];
let health = null;
const photoUrls = new Map(); // "job/load/name" -> object URL

// ---- Requests -------------------------------------------------------------------------

// This page's own saved key, else the iPad app's when the app uses this same server.
function key() {
  try {
    const own = localStorage.getItem(KEY);
    if (own) return own;
    const app = JSON.parse(localStorage.getItem('terra.api') ?? 'null');
    return app && !app.url ? app.key ?? '' : '';
  } catch { return ''; }
}

// { token, until } while unlocked. The iPad app writes the same entry.
function unlockEntry() {
  try {
    const e = JSON.parse(localStorage.getItem(UNLOCK) ?? 'null');
    return e?.token && new Date(e.until).getTime() > Date.now() ? e : null;
  } catch { return null; }
}

function adminToken() {
  return unlockEntry()?.token ?? '';
}

function setAdminToken(token, until) {
  try {
    if (token) localStorage.setItem(UNLOCK, JSON.stringify({ token, until: until ?? unlockEntry()?.until }));
    else localStorage.removeItem(UNLOCK);
  } catch { /* private window */ }
  drawLockButton();
}

function drawLockButton() {
  const e = unlockEntry();
  document.body.classList.toggle('unlocked', !!e);
  $('#btn-lock').hidden = !e;
  if (e) $('#lock-left').textContent = `Lock · ${Math.max(1, Math.ceil((new Date(e.until).getTime() - Date.now()) / 60000))} min`;
}

class ApiError extends Error {
  constructor(message, status, problem) {
    super(message);
    this.status = status;
    this.problem = problem ?? {};
  }
}

async function api(path, { method = 'GET', body } = {}) {
  const token = adminToken();
  const res = await fetch(`../api${path}`, {
    method,
    headers: {
      Authorization: `Bearer ${key()}`,
      ...(token ? { 'X-Terra-Admin': token } : {}),
      ...(body ? { 'Content-Type': 'application/json' } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
    cache: 'no-store',
  });
  if (res.status === 401) {
    askKey('That key was refused.');
    throw new ApiError('The access key was refused.', 401);
  }
  if (!res.ok) {
    const problem = await res.json().catch(() => null);
    // The admin code timed out (or the server restarted): back to the lock screen.
    if (res.status === 403 && problem?.locked) setAdminToken('');
    throw new ApiError(problem?.detail || problem?.title || `Server error ${res.status}`, res.status, problem);
  }
  if (res.status === 204) return null;
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

function bytes(n) {
  const v = Number(n) || 0;
  if (v < 1024) return `${v} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let x = v / 1024;
  let i = 0;
  while (x >= 1024 && i < units.length - 1) { x /= 1024; i++; }
  return `${x.toFixed(x < 10 ? 1 : 0)} ${units[i]}`;
}

function duration(seconds) {
  const s = Math.max(0, Math.round(seconds));
  const d = Math.floor(s / 86400);
  const h = Math.floor((s % 86400) / 3600);
  const m = Math.floor((s % 3600) / 60);
  return d ? `${d} d ${h} h` : h ? `${h} h ${m} min` : m ? `${m} min` : `${s} s`;
}

const num = (n) => (Number(n) || 0).toLocaleString('en-US');

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
    <dt>iPad app</dt><dd>${health.client.served ? `v${esc(health.client.version ?? '?')}` : '<span class="error">Not served: the iPad app folder (CLIENT_ROOT) has no index.html</span>'}</dd>
    <dt>PowerFab bridge</dt><dd>${c?.ok ? `Reachable (${num(c.ms)} ms) · ${esc(c.health?.mode ?? '')} mode · writes ${c.health?.writesEnabled ? 'on' : '<span class="error">off</span>'}` : `<span class="error">${esc(c?.error ?? 'Unreachable')}</span>`}</dd>
    <dt>Photos</dt><dd>${health.photosEnabled ? 'On' : 'Off'}</dd>
    <dt>Admin code</dt><dd>${health.adminPinSet ? 'Set' : '<span class="warn-text">Not set yet:</span> <a href="#/settings">set one</a> to lock Server and Settings'}</dd>
  </dl>
  <p><a href="#/server">Full server stats and health ›</a></p>`;
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

// Every job PowerFab lets this server reach (the list the iPads pick from), with what the server has
// for each and where its photos go.
let jobFilter = '';

async function pageJobs() {
  const r = await api('/jobs/powerfab');
  const inPf = r.jobs.filter((j) => j.inPowerFab).length;
  return `<section class="panel">
    <h2>Jobs <span class="muted">· ${r.ok ? `${inPf} in PowerFab` : 'PowerFab not reachable'}</span></h2>
    ${r.ok ? `<p class="muted">Every job PowerFab gives this server's PowerFab user, open or closed: the same list the iPads pick from. Read ${esc(when(r.fetchedAt))}.</p>`
      : `<div class="banner bad"><span>Couldn’t read PowerFab’s job list: ${esc(r.error)}</span><a class="btn" href="#/settings">Settings</a></div>`}
    <input class="filter" type="search" id="job-filter" placeholder="Filter jobs" value="${esc(jobFilter)}" autocomplete="off">
    ${r.jobs.length ? `<div style="overflow-x:auto"><table id="jobs-table"><thead><tr><th>Job</th><th>Description</th><th>Location</th><th class="num">Loads here</th><th class="num">Pieces in stacks</th><th>Last activity</th>${r.photosEnabled ? '<th>Photos go to</th>' : ''}</tr></thead><tbody>
    ${r.jobs.map((j) => `<tr class="${j.inPowerFab ? '' : 'not-pf'}" data-search="${esc(`${j.job} ${j.description} ${j.location}`.toLowerCase())}">
      <td><a href="#/job/${enc(j.job)}"><b>${esc(j.job)}</b></a>${j.inPowerFab ? '' : ' <span class="pill warn" title="The server has stacks for it but PowerFab didn’t list it">not in PowerFab</span>'}</td>
      <td>${esc(j.description)}</td><td>${esc(j.location)}</td>
      <td class="num">${j.loads || ''}</td><td class="num">${j.pieces || ''}</td><td>${j.lastAt ? esc(ago(j.lastAt)) : ''}</td>
      ${r.photosEnabled ? `<td class="path">${j.photoError ? `<span class="error">${esc(j.photoError)}</span>` : `${esc(j.photoFolder ?? '')}<br><span class="muted">${j.photoRule === 'default' ? 'default' : `rule ${esc(j.photoRule ?? '')}`}</span>`}</td>` : ''}
    </tr>`).join('')}
    </tbody></table></div>` : '<p class="muted">No jobs.</p>'}
    ${r.photosEnabled ? '<p class="muted">Photo folders come from the folder rules: <a href="#/settings">Settings › Photo folders</a>.</p>' : ''}
  </section>`;
}

function filterJobs() {
  const q = jobFilter.trim().toLowerCase();
  document.querySelectorAll('#jobs-table tbody tr').forEach((tr) => { tr.hidden = q && !tr.dataset.search.includes(q); });
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

// ---- Admin code -------------------------------------------------------------------------

const PIN_KEYS = ['7', '8', '9', '4', '5', '6', '1', '2', '3', 'clear', '0', 'back'];
let pad = null;

function renderPin() {
  const display = $('#pin-display');
  display.innerHTML = pad.value ? '<i></i>'.repeat(pad.value.length) : '<span class="placeholder">4 to 8 digits</span>';
  $('#pin-ok').disabled = pad.value.length < 4;
}

function pressPin(k) {
  if (!pad) return;
  if (k === 'back') pad.value = pad.value.slice(0, -1);
  else if (k === 'clear') pad.value = '';
  else if (/^\d$/.test(k) && pad.value.length < 8) pad.value += k;
  $('#pin-error').textContent = '';
  renderPin();
}

function closePin(result) {
  const p = pad;
  pad = null;
  if ($('#dlg-pin').open) $('#dlg-pin').close();
  p?.resolve(result);
}

// Opens the keypad. Resolves with the digits entered, or null on Cancel.
function openPinPad({ title = 'Admin code', note = '', error = '', okLabel = 'Unlock' } = {}) {
  if (pad) closePin(null);
  return new Promise((resolve) => {
    pad = { value: '', resolve };
    $('#pin-title').textContent = title;
    $('#pin-note').textContent = note;
    $('#pin-error').textContent = error;
    $('#pin-ok').textContent = okLabel;
    $('#pin-keys').innerHTML = PIN_KEYS.map((k) => `<button class="btn key${k.length > 1 ? ' key-fn' : ''}" type="button" data-pin="${k}">${k === 'back' ? '&#9003;' : k === 'clear' ? 'Clear' : k}</button>`).join('');
    const dlg = $('#dlg-pin');
    dlg.classList.remove('shake');
    if (error) requestAnimationFrame(() => dlg.classList.add('shake'));
    renderPin();
    if (!dlg.open) dlg.showModal();
    $('#pin-ok').focus({ preventScroll: true });
  });
}

// First visit: make a code (twice). After that: enter it. Resolves true once unlocked.
async function unlock() {
  let lock;
  try { lock = await api('/admin/lock'); } catch (err) { return false; }
  if (lock.unlocked) { drawLockButton(); return true; }
  if (!lock.pinSet) {
    let error = '';
    for (;;) {
      const first = await openPinPad({ title: 'Set an admin code', note: 'No admin code is set yet. Pick 4 to 8 digits: it will be needed to open the admin board and change server settings, here and on the iPads.', error, okLabel: 'Next' });
      if (first === null) return false;
      const again = await openPinPad({ title: 'Enter it again', note: 'The same code, to be sure.', okLabel: 'Set code' });
      if (again === null) return false;
      if (first !== again) { error = 'The two codes didn’t match. Start again.'; continue; }
      try {
        const r = await api('/admin/setup', { method: 'POST', body: { pin: first } });
        setAdminToken(r.token, r.until);
        return true;
      } catch (err) {
        error = err.message;
        if (err.status === 409) return unlock();
      }
    }
  }
  let error = lock.retryAfter ? `Too many wrong codes. Try again in ${lock.retryAfter} s.` : '';
  for (;;) {
    const pin = await openPinPad({ title: 'Admin code', note: 'The admin board is locked. It stays open on this device for 30 minutes after the code.', error });
    if (pin === null) return false;
    try {
      const r = await api('/admin/unlock', { method: 'POST', body: { pin } });
      setAdminToken(r.token, r.until);
      return true;
    } catch (err) {
      error = err.message;
    }
  }
}

function lockedPage() {
  return `<section class="panel locked-panel">
    <div class="big" aria-hidden="true">🔒</div>
    <h1>The admin board is locked</h1>
    <p class="muted">Enter the admin code (the same one the iPads use). This device then stays unlocked for 30 minutes.</p>
    <p><button class="btn btn-lg primary" type="button" data-unlock>Enter code</button></p>
  </section>`;
}

async function lockNow() {
  try { await api('/admin/lock', { method: 'POST' }); } catch { /* locked anyway */ }
  setAdminToken('');
  draw();
}

// Runs a locked page: asks for the code first when needed.
async function locked(what, page) {
  // The keypad opens by itself when the page is opened, not on a live redraw after Cancel.
  const ask = redrawReason === 'nav';
  if (!adminToken()) {
    if (ask) setTimeout(async () => { if (await unlock()) draw(); }, 0);
    return lockedPage();
  }
  try {
    return await page();
  } catch (err) {
    if (err.status === 403 && err.problem?.locked) {
      if (ask) setTimeout(async () => { if (await unlock()) draw(); }, 0);
      return lockedPage();
    }
    throw err;
  }
}

// ---- Server: stats and health (locked) ---------------------------------------------------

function tile(value, label, sub = '', bad = false) {
  return `<div class="tile${bad ? ' bad' : ''}"><b>${value}</b><span>${esc(label)}</span>${sub ? `<small>${sub}</small>` : ''}</div>`;
}

// Bars, one per slot, each with up to two stacked parts: a (blue) and b (red).
function bars(points, { a, b, aLabel, bLabel, label }) {
  const max = Math.max(1, ...points.map((p) => (p[a] || 0) + (b ? p[b] || 0 : 0)));
  return `<div class="legend"><span><i style="background:var(--blue)"></i>${esc(aLabel)}</span>${b ? `<span><i style="background:var(--bad)"></i>${esc(bLabel)}</span>` : ''}<span style="margin-left:auto">most: ${num(max)}</span></div>
  <div class="bars">${points.map((p) => `<div class="bar" title="${esc(`${label(p)}: ${num(p[a])} ${aLabel.toLowerCase()}${b ? `, ${num(p[b])} ${bLabel.toLowerCase()}` : ''}`)}">
    <span class="seg a" style="height:${((p[a] || 0) / max) * 100}%"></span>${b && p[b] ? `<span class="seg b" style="height:${(p[b] / max) * 100}%"></span>` : ''}</div>`).join('')}</div>`;
}

function diskMeter(d) {
  const used = 1 - d.disk.freeBytes / d.disk.totalBytes;
  const cls = d.disk.freeBytes < 5 * 2 ** 30 || used > 0.95 ? 'bad' : used > 0.85 ? 'warn' : '';
  return `<div style="margin-bottom:.75rem"><b>${esc(d.name)}</b> <span class="muted">${esc(d.disk.drive)}</span>
    <div class="meter ${cls}"><i style="width:${(used * 100).toFixed(1)}%"></i></div>
    <span class="muted">${bytes(d.disk.freeBytes)} free of ${bytes(d.disk.totalBytes)}</span></div>`;
}

// The last 14 days, with days that had nothing filled in.
function lastDays(days) {
  const byDay = new Map(days.map((d) => [d.day, d]));
  const out = [];
  for (let i = 13; i >= 0; i--) {
    const d = new Date();
    d.setDate(d.getDate() - i);
    const day = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
    out.push(byDay.get(day) ?? { day, scans: 0, refused: 0, checks: 0, photos: 0 });
  }
  return out;
}

async function pageServer() {
  const s = await api('/admin/stats');
  const p = s.process;
  const r = s.requests;
  const d = s.database.data;
  const c = s.conversion;
  const dayLabel = (x) => new Date(`${x.day}T12:00`).toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' });
  const days = lastDays(d.days);
  const problems = [
    !c.ok && `PowerFab bridge: ${c.error}`,
    !s.client.served && 'The iPad app isn’t being served (iPad app folder has no index.html).',
    !s.accessKeySet && 'No access key is set: anyone who can reach the server can use it.',
    s.photos.rulesError && `Photo folder rules: ${s.photos.rulesError}`,
    ...s.disks.filter((x) => x.disk.freeBytes < 5 * 2 ** 30).map((x) => `${x.name} drive has only ${bytes(x.disk.freeBytes)} free.`),
  ].filter(Boolean);
  return `
  ${s.restartNeeded ? '<div class="banner"><span>Some saved settings take effect after a restart.</span><a class="btn" href="#/settings">Settings</a></div>' : ''}
  ${problems.length ? `<div class="banner bad"><span><b>Needs attention</b><br>${problems.map(esc).join('<br>')}</span></div>` : '<div class="banner" style="background:#17391f;border-color:var(--ok)"><span class="ok-text"><b>✓ All good</b></span><span class="muted">PowerFab bridge reachable, app served, no problems found.</span></div>'}
  <div class="tiles">
    ${tile(duration(p.uptimeSeconds), 'Up for', `since ${esc(when(p.startedAt))}`)}
    ${tile(`${s.connections.ipads}`, 'iPads connected', `${s.connections.devices} known · ${s.connections.admins} admin page${s.connections.admins === 1 ? '' : 's'}`)}
    ${tile(c.ok ? `${num(c.ms)} ms` : 'Down', 'PowerFab bridge', c.ok ? `${esc(c.health?.mode ?? '')} · writes ${c.health?.writesEnabled ? 'on' : 'off'}` : esc(c.error ?? ''), !c.ok)}
    ${tile(num(d.today.scans), 'Scans today', `${num(d.today.refused)} refused · ${num(d.today.checks)} truck checks`, false)}
    ${tile(num(d.today.photos), 'Photos today', `${num(d.counts.photos)} in all · ${bytes(d.counts.photoBytes)}`)}
    ${tile(num(r.lastHour.requests), 'Requests, last hour', `${num(r.lastHour.averageMs)} ms average`)}
    ${tile(num(r.lastHour.errors), 'Server errors, last hour', `${num(r.totalErrors)} since start`, r.lastHour.errors > 0)}
    ${tile(num(r.lastHour.bridgeCalls), 'PowerFab calls, last hour', `${num(r.lastHour.bridgeAverageMs)} ms average${r.lastHour.bridgeErrors ? ` · ${r.lastHour.bridgeErrors} failed` : ''}`, r.lastHour.bridgeErrors > 0)}
    ${tile(bytes(p.memoryBytes), 'Memory', `${p.threads} threads · ${p.cpuSeconds} s CPU`)}
    ${tile(bytes(s.database.bytes + s.database.walBytes), 'Database', `${num(d.counts.loads)} loads · ${num(d.counts.activity)} history lines`)}
  </div>
  <div class="grid">
    <div>
      <section class="panel"><h2>Scans, last 14 days</h2>
        ${bars(days, { a: 'scans', b: 'refused', aLabel: 'Scans', bLabel: 'Refused', label: dayLabel })}
        <div class="bars-axis"><span>${esc(dayLabel(days[0]))}</span><span>Today</span></div>
      </section>
      <section class="panel"><h2>Requests, last hour</h2>
        ${bars(r.perMinute, { a: 'requests', b: 'errors', aLabel: 'Requests', bLabel: 'Errors', label: (x) => new Date(x.minute).toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' }) })}
        <div class="bars-axis"><span>60 min ago</span><span>Now</span></div>
      </section>
      <section class="panel"><h2>iPads, last 7 days</h2>
        ${d.byDevice.length ? `<table><thead><tr><th>iPad</th><th class="num">Scans today</th><th class="num">7 days</th><th>Last scan</th></tr></thead><tbody>
        ${d.byDevice.map((x) => `<tr><td>${who(x.deviceId, x.deviceName)}</td><td class="num">${num(x.today)}</td><td class="num">${num(x.week)}</td><td>${x.lastAt ? esc(ago(x.lastAt)) : ''}</td></tr>`).join('')}</tbody></table>` : '<p class="muted">No scans in the last 7 days.</p>'}
      </section>
      <section class="panel"><h2>What happened, last 7 days</h2>
        ${d.kinds.length ? `<table><tbody>${d.kinds.map((k) => `<tr><td><span class="kind ${esc(k.kind)}">${esc(k.kind.replace('-', ' '))}</span></td><td class="num">${num(k.count)}</td></tr>`).join('')}</tbody></table>` : '<p class="muted">Nothing.</p>'}
      </section>
    </div>
    <div>
      <section class="panel"><h2>Warnings and errors</h2>
        <p class="muted">The server’s last warnings and errors since it started (the full log is in the Windows Event Log).</p>
        ${r.log.length ? `<ul class="log">${r.log.map((l) => `<li><time title="${esc(new Date(l.at).toLocaleString())}">${esc(when(l.at))}</time><span class="${esc(l.level)}">${esc(l.level)}</span><span class="msg">${esc(l.message)} <span class="muted">${esc(l.category)}</span></span></li>`).join('')}</ul>` : '<p class="ok-text">None.</p>'}
      </section>
      <section class="panel"><h2>Disk space</h2>${s.disks.length ? s.disks.map(diskMeter).join('') : '<p class="muted">Couldn’t read.</p>'}</section>
      <section class="panel"><h2>Server</h2>
        <dl class="health">
          <dt>Version</dt><dd>v${esc(s.version)}</dd>
          <dt>Computer</dt><dd>${esc(p.machine)} · ${p.processors} CPUs</dd>
          <dt>System</dt><dd>${esc(p.os)} · .NET ${esc(p.dotnet)}</dd>
          <dt>Running as</dt><dd>${p.service ? 'Windows service' : 'a console program (not the service)'}</dd>
          <dt>iPad app</dt><dd>${s.client.served ? `v${esc(s.client.version ?? '?')}` : '<span class="error">not served</span>'} from <code>${esc(s.client.root || '(not set)')}</code></dd>
          <dt>Database</dt><dd><code>${esc(s.database.path)}</code><br>${bytes(s.database.bytes)} + ${bytes(s.database.walBytes)} log</dd>
          <dt>Holds</dt><dd>${num(d.counts.jobs)} jobs · ${num(d.counts.loads)} loads · ${num(d.counts.stacks)} stacks · ${num(d.counts.pieces)} pieces · ${num(d.counts.checkScans)} truck check scans · ${num(d.counts.devices)} iPads</dd>
          <dt>Photos</dt><dd>${s.photos.enabled ? `On · <code>${esc(s.photos.root)}</code> · ${s.photos.rules} folder rule${s.photos.rules === 1 ? '' : 's'}` : 'Off'}</dd>
          <dt>Requests</dt><dd>${num(r.totalRequests)} since start</dd>
        </dl>
        <div class="form-foot"><a class="btn" href="../api/admin/backup" data-download>Download a database backup</a></div>
      </section>
    </div>
  </div>
  <p class="muted">Updates every 10 seconds.</p>`;
}

// ---- Settings (locked) -------------------------------------------------------------------

let rulesDraft = null; // the photo rules being edited, kept across live redraws

function field(s, view) {
  const id = `set-${s.key}`;
  const tags = `${s.restart ? '' : '<span class="tag">applies now</span>'}${s.pending ? '<span class="tag pending">saved · restart to apply</span>' : ''}`;
  let input;
  if (s.kind === 'bool') {
    input = `<label class="switch"><input type="checkbox" id="${id}" data-key="${s.key}" ${s.value === 'true' ? 'checked' : ''}> ${esc(s.label)}</label>`;
    return `<div class="field">${input}${tags}<div class="help">${esc(s.help)}</div></div>`;
  }
  if (s.kind === 'types') {
    const on = new Set(String(s.value || s.default).split(',').map((t) => t.trim()));
    input = `<div class="checks" id="${id}" data-key="${s.key}" data-kind="types">${view.photoTypes.map((t) => `<label><input type="checkbox" value="${t}" ${on.has(t) ? 'checked' : ''}> ${t}</label>`).join('')}</div>`;
  } else if (s.kind === 'secret') {
    input = `<input type="password" id="${id}" data-key="${s.key}" data-kind="secret" autocomplete="new-password" placeholder="${s.set ? 'Set · leave blank to keep it' : 'Not set'}">`;
  } else if (s.kind === 'number') {
    input = `<input type="number" id="${id}" data-key="${s.key}" min="1" step="any" value="${esc(s.value)}">`;
  } else if (s.key === 'CONVERSION_URL') {
    input = `<div class="row"><input type="text" id="${id}" data-key="${s.key}" value="${esc(s.value)}" spellcheck="false"><button class="btn" type="button" data-test-conversion>Test</button></div><div class="help" id="conversion-test"></div>`;
  } else {
    input = `<input type="text" id="${id}" data-key="${s.key}" value="${esc(s.value)}" spellcheck="false">`;
  }
  return `<div class="field"><label for="${id}">${esc(s.label)}${tags}</label>${input}<div class="help">${esc(s.help)}${s.default ? ` Default: <code>${esc(s.default)}</code>.` : ''}</div></div>`;
}

function rulesEditor(r) {
  const d = rulesDraft;
  return `<section class="panel" id="rules">
    <h2>Photo folders</h2>
    <p class="muted">Which folder each job’s photos go in. Rules are checked top to bottom; the first whose pattern matches the job number wins.
      Saving takes effect at once, no restart. File: <code>${esc(r.path)}</code>${r.exists ? '' : ' (not made yet)'}.${r.photosEnabled ? '' : ' <span class="warn-text">Photos are off (Photos section above).</span>'}</p>
    ${r.error ? `<div class="banner bad"><span>The rules file on disk is broken: ${esc(r.error)}. Saving here replaces it (the old one is kept as .bak).</span></div>` : ''}
    <div class="form-grid">
      <div class="field"><label for="rules-default">Default job folder</label>
        <input type="text" id="rules-default" value="${esc(d.default ?? '')}" placeholder="${esc(r.root ? `${r.root}\\{job}` : 'e.g. D:\\TerraPhotos\\{job}')}" spellcheck="false">
        <div class="help">For jobs no rule matches. Blank: <code>${esc(r.root || 'PHOTOS_ROOT')}\\{job}</code>. Can use <code>{job}</code>.</div></div>
      <div class="field"><label for="rules-load">Load folder name</label>
        <input type="text" id="rules-load" value="${esc(d.loadFolder ?? '{load}')}" spellcheck="false">
        <div class="help">One folder per load, inside the job folder. Can use <code>{load}</code> and <code>{job}</code>, e.g. <code>Load {load}</code>.</div></div>
      <div class="field"><label>Rules</label>
        <div class="rules">${d.rules.length ? d.rules.map((x, i) => `<div class="rule" data-rule="${i}">
          <span class="n">${i + 1}</span>
          <input type="text" data-rule-match value="${esc(x.match)}" placeholder="Job pattern (regex), e.g. ^26\\d{3}$" spellcheck="false" aria-label="Rule ${i + 1} pattern">
          <input type="text" data-rule-folder value="${esc(x.folder)}" placeholder="Job folder, e.g. \\\\fileserver\\Jobs\\{job}\\Photos" spellcheck="false" aria-label="Rule ${i + 1} folder">
          <span class="tools"><button class="btn" type="button" data-rule-up ${i === 0 ? 'disabled' : ''} aria-label="Move up">↑</button><button class="btn" type="button" data-rule-down ${i === d.rules.length - 1 ? 'disabled' : ''} aria-label="Move down">↓</button><button class="btn" type="button" data-rule-remove aria-label="Remove">✕</button></span>
        </div>`).join('') : '<p class="muted">No rules: every job uses the default folder.</p>'}</div>
        <div class="form-foot"><button class="btn" type="button" data-rule-add>Add a rule</button></div>
        <div class="help">A pattern is a regex tested against the job number, ignoring case: <code>^25-\\d+$</code> matches 25-001, 25-002… A folder is a full path and can use <code>{job}</code> and the pattern’s named groups, e.g. pattern <code>^(?&lt;year&gt;\\d{2})\\d{3}$</code> with folder <code>\\\\fileserver\\Jobs\\20{year}\\{job}</code>. See Docs › Photos.</div>
      </div>
      <div class="field"><label for="rules-test-job">Try a job number</label>
        <div class="row"><input type="text" id="rules-test-job" placeholder="Job number, e.g. 26101" spellcheck="false"><input type="text" id="rules-test-load" placeholder="Load" style="max-width:7rem" value="1"><button class="btn" type="button" data-rules-test>Show folder</button></div>
        <div class="test-out" id="rules-test-out" hidden></div>
        <div class="help">Uses the rules on screen, saved or not.</div></div>
    </div>
    <div class="form-foot"><button class="btn primary" type="button" data-rules-save>Save photo folders</button><button class="btn" type="button" data-rules-reset>Undo changes</button><span class="muted" id="rules-saved"></span></div>
  </section>`;
}

async function pageSettings() {
  const [view, rules, updates] = await Promise.all([api('/admin/settings'), api('/admin/photo-rules'), api('/admin/update')]);
  if (!rulesDraft) rulesDraft = { default: rules.default ?? '', loadFolder: rules.loadFolder, rules: rules.rules.map((x) => ({ ...x })) };
  const groups = [...new Set(view.settings.map((s) => s.group))];
  return `
  ${view.restartNeeded.length ? `<div class="banner"><span><b>Restart needed.</b> Saved changes to ${esc(view.restartNeeded.map((k) => view.settings.find((s) => s.key === k)?.label ?? k).join(', '))} take effect after the server restarts. The iPads reconnect by themselves.</span>
    ${view.canRestart ? '<button class="btn primary" type="button" data-restart>Restart server now</button>' : '<span class="muted">Not running as the Windows service: restart it by hand.</span>'}</div>` : ''}
  <div class="grid">
    <div>
      <form class="panel" id="form-settings">
        <h2>Server settings</h2>
        <p class="muted">Saved to <code>${esc(view.file)}</code> (the old file is kept as .bak). A setting that would stop the server from starting is refused.</p>
        ${groups.map((g) => `<h3>${esc(g)}</h3><div class="form-grid">${view.settings.filter((s) => s.group === g).map((s) => field(s, view)).join('')}</div>`).join('')}
        <div class="form-foot"><button class="btn primary" type="submit">Save settings</button><span class="muted" id="settings-saved"></span></div>
      </form>
    </div>
    <div>
      ${rulesEditor(rules)}
      <section class="panel">
        <h2>Admin code</h2>
        <p class="muted">Locks Server and Settings on this page. It is kept on the server only as a hash. Forgot it? Remove <code>ADMIN_PIN_HASH</code> from <code>${esc(view.file)}</code> on the server and restart; the next visit asks for a new one.</p>
        <div class="form-foot"><button class="btn" type="button" data-change-pin>Change the code</button><button class="btn" type="button" data-lock-now>Lock now</button><span class="muted" id="pin-saved"></span></div>
      </section>
      <section class="panel" id="updates">${updatesPanel(updates)}</section>
      <section class="panel">
        <h2>Maintenance</h2>
        <div class="form-foot" style="margin-top:0">
          <a class="btn" href="../api/admin/backup" data-download>Download a database backup</a>
          ${view.canRestart ? '<button class="btn" type="button" data-restart>Restart server</button>' : ''}
        </div>
        <p class="muted">The backup is a copy of <code>terra.db</code> (stacks, devices, history, truck checks, photo records) taken while the server runs. The database folder is <code>${esc(view.dataDir)}</code>; it isn’t changed here.</p>
      </section>
    </div>
  </div>`;
}

function updatesPanel(u) {
  const c = u.client;
  const why = !u.supported ? 'Updates run only on the Windows server.' : '';
  return `<h2>Updates</h2>
    <p class="muted">The iPad app is served from <code>${esc(u.clientRoot || '(no iPad app folder set)')}</code>, now v${esc(u.clientVersion ?? '?')}.
      <b>Update iPad app</b> runs <code>update-client.ps1</code>: it gets the latest terra-tekla-client from GitHub. No restart; the iPads get it when they press Update or next open the app.
      <b>Update server</b> runs <code>deploy.ps1</code> from <code>${esc(u.serverRepo ?? 'SERVER_REPO (not set)')}</code>: it gets the latest server, builds it, restarts the service and updates the iPad app too. The iPads reconnect by themselves.</p>
    ${why ? `<p class="warn-text">${esc(why)}</p>` : ''}
    <div class="form-foot" style="margin-top:0">
      <button class="btn primary" type="button" data-update="client" ${!u.supported || !u.clientScript || c?.running ? 'disabled' : ''}>${c?.running ? 'Updating iPad app…' : 'Update iPad app'}</button>
      <button class="btn" type="button" data-update="server" ${!u.supported || !u.service || !u.serverScript ? 'disabled' : ''}>Update server</button>
      <span class="muted" id="update-result"></span>
    </div>
    ${u.supported && !u.serverScript ? '<p class="muted">Update server needs <b>Server git clone</b> (Updates, in the settings on the left) set to the terra-tekla-server git clone.</p>' : ''}
    ${c ? `<h3>iPad app update ${c.running ? '(running)' : c.exitCode === 0 ? `<span class="ok-text">done ${esc(when(c.finishedAt))}</span>` : `<span class="error">failed ${esc(when(c.finishedAt))}</span>`}</h3>
      <pre class="test-out" style="white-space:pre-wrap;max-height:220px;overflow:auto">${esc(c.output.join('\n') || '…')}</pre>` : ''}
    ${u.server ? `<h3>Last server update <span class="muted">${esc(when(u.server.changedAt))}</span></h3>
      <pre class="test-out" style="white-space:pre-wrap;max-height:220px;overflow:auto">${esc(u.server.output.join('\n'))}</pre>` : ''}`;
}

let updatePoll = null;

async function refreshUpdates() {
  clearTimeout(updatePoll);
  if (currentPage !== 'settings' || !$('#updates')) return;
  try {
    const u = await api('/admin/update');
    $('#updates').innerHTML = updatesPanel(u);
    if (u.client?.running) updatePoll = setTimeout(refreshUpdates, 1500);
  } catch { /* locked or gone: the next draw shows it */ }
}

async function startUpdate(which) {
  const out = $('#update-result');
  if (which === 'server' && !confirm('Update the server now? It gets the latest code from GitHub, builds it and restarts. The iPads lose the connection for about a minute and reconnect by themselves.')) return;
  try {
    await api(`/admin/update/${which}`, { method: 'POST' });
  } catch (err) {
    out.textContent = err.message;
    out.className = 'error';
    return;
  }
  if (which === 'client') { refreshUpdates(); return; }
  $('#main').innerHTML = '<section class="panel locked-panel"><h1>Updating the server…</h1><p class="muted" id="restart-wait">deploy.ps1 is pulling, building and restarting. This page reloads when the server is back; Settings › Updates then shows how it went.</p></section>';
  await waitForServer(240000, true);
}

function readSettingsForm() {
  const changes = {};
  for (const el of document.querySelectorAll('#form-settings [data-key]')) {
    const k = el.dataset.key;
    if (el.dataset.kind === 'types') changes[k] = [...el.querySelectorAll('input:checked')].map((i) => i.value).join(',');
    else if (el.type === 'checkbox') changes[k] = el.checked ? 'true' : 'false';
    else if (el.dataset.kind === 'secret') { if (el.value !== '') changes[k] = el.value; }
    else changes[k] = el.value;
  }
  return changes;
}

function readRules() {
  rulesDraft = {
    default: $('#rules-default')?.value ?? '',
    loadFolder: $('#rules-load')?.value ?? '{load}',
    rules: [...document.querySelectorAll('[data-rule]')].map((row) => ({
      match: row.querySelector('[data-rule-match]').value,
      folder: row.querySelector('[data-rule-folder]').value,
    })),
  };
  return rulesDraft;
}

function redrawRules() {
  const scroll = window.scrollY;
  api('/admin/photo-rules').then((r) => {
    $('#rules').outerHTML = rulesEditor(r);
    window.scrollTo(0, scroll);
  }).catch((err) => { $('#rules-saved').textContent = err.message; });
}

async function restartServer() {
  if (!confirm('Restart the server now? The iPads lose the connection for a few seconds and reconnect by themselves.')) return;
  try {
    await api('/admin/restart', { method: 'POST' });
  } catch (err) {
    alert(err.message);
    return;
  }
  $('#main').innerHTML = '<section class="panel locked-panel"><h1>Restarting…</h1><p class="muted" id="restart-wait">Waiting for the server to come back.</p></section>';
  await waitForServer(120000, false);
}

// Waits for the server to go away (when it should) and answer again, then reloads the page.
async function waitForServer(limit, expectDown) {
  const started = Date.now();
  const up = async () => {
    try { return (await fetch('../health', { cache: 'no-store' })).ok; } catch { return false; }
  };
  await new Promise((r) => setTimeout(r, 4000));
  if (expectDown) while (await up() && Date.now() - started < limit) await new Promise((r) => setTimeout(r, 2000));
  while (!(await up())) {
    if (Date.now() - started > limit) {
      $('#restart-wait').textContent = `It hasn’t come back after ${Math.round(limit / 60000)} minutes. Check the TerraTeklaServer service on the server, and data\\deploy-last.log.`;
      return;
    }
    await new Promise((r) => setTimeout(r, 1500));
  }
  setAdminToken(''); // sessions don't survive a restart
  location.hash = '#/settings';
  location.reload();
}

async function changePin() {
  const current = await openPinPad({ title: 'Current code', note: 'Enter the code in use now.', okLabel: 'Next' });
  if (current === null) return;
  let error = '';
  for (;;) {
    const pin = await openPinPad({ title: 'New code', note: '4 to 8 digits.', error, okLabel: 'Next' });
    if (pin === null) return;
    const again = await openPinPad({ title: 'New code again', note: 'The same new code, to be sure.', okLabel: 'Change code' });
    if (again === null) return;
    if (pin !== again) { error = 'The two codes didn’t match. Start again.'; continue; }
    try {
      const r = await api('/admin/pin', { method: 'PUT', body: { current, pin } });
      setAdminToken(r.token, r.until);
      $('#pin-saved').textContent = 'Code changed. Other browsers have to enter the new one.';
    } catch (err) {
      $('#pin-saved').textContent = err.message;
      if (err.problem?.locked) draw();
    }
    return;
  }
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
  currentPage = page;
  try {
    const html = await locked('The admin board', () => page === 'device' ? pageDevice(arg)
      : page === 'jobs' ? pageJobs()
      : page === 'server' ? pageServer()
      : page === 'settings' ? pageSettings()
      : page === 'job' ? pageJob(arg)
      : page === 'load' ? pageLoad(arg, query)
      : page === 'mark' ? pageMark(arg, query)
      : page === 'photos' ? pagePhotos()
      : page === 'docs' ? pageDocs(arg || 'setup')
      : pageHome());
    if (seq !== drawing) return;
    const scroll = window.scrollY;
    $('#main').innerHTML = html;
    window.scrollTo(0, scroll);
    fillPhotos();
    if (page === 'jobs') filterJobs();
    if (page === 'settings') refreshUpdates();
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
let currentPage = '';

// Live changes redraw the page, at most a few times a second. Settings is never redrawn by itself,
// so nothing being typed there is lost.
function redrawSoon() {
  if (currentPage === 'settings' || $('#dlg-pin').open) return;
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
  $('#main').addEventListener('submit', async (e) => {
    if (e.target.id === 'form-settings') {
      e.preventDefault();
      const out = $('#settings-saved');
      out.textContent = 'Saving…';
      try {
        await api('/admin/settings', { method: 'PUT', body: readSettingsForm() });
        readRules();
        await draw();
        $('#settings-saved').textContent = 'Saved.';
      } catch (err) {
        out.textContent = err.message;
        out.className = 'error';
      }
      return;
    }
    if (e.target.id !== 'form-device') return;
    e.preventDefault();
    saveDevice(e.target, { name: $('#device-name').value });
  });
  $('#main').addEventListener('input', (e) => {
    if (e.target.id === 'job-filter') { jobFilter = e.target.value; filterJobs(); }
  });
  $('#main').addEventListener('click', async (e) => {
    const t = e.target;
    if (t.closest('[data-unlock]')) { if (await unlock()) draw(); return; }
    if (t.closest('[data-restart]')) { restartServer(); return; }
    if (t.closest('[data-update]')) { startUpdate(t.closest('[data-update]').dataset.update); return; }
    if (t.closest('[data-change-pin]')) { changePin(); return; }
    if (t.closest('[data-lock-now]')) { lockNow(); return; }
    if (t.closest('[data-download]')) {
      // A plain link can't send the key and code, so fetch it and hand the browser the file.
      e.preventDefault();
      const link = t.closest('[data-download]');
      link.textContent = 'Preparing…';
      try {
        const res = await fetch(link.href, { headers: { Authorization: `Bearer ${key()}`, 'X-Terra-Admin': adminToken() } });
        if (!res.ok) throw new Error(res.status === 403 ? 'Locked: enter the admin code again.' : `Server error ${res.status}`);
        const name = /filename="?([^";]+)/.exec(res.headers.get('content-disposition') ?? '')?.[1] ?? 'terra-backup.db';
        const a = document.createElement('a');
        a.href = URL.createObjectURL(await res.blob());
        a.download = name;
        a.click();
        setTimeout(() => URL.revokeObjectURL(a.href), 10000);
        link.textContent = 'Download a database backup';
      } catch (err) {
        link.textContent = err.message;
      }
      return;
    }
    if (t.closest('[data-test-conversion]')) {
      const out = $('#conversion-test');
      out.textContent = 'Trying…';
      try {
        const r = await api('/admin/test-conversion', { method: 'POST', body: { url: $('#set-CONVERSION_URL').value } });
        out.innerHTML = r.ok ? `<span class="ok-text">✓ Answered in ${num(r.ms)} ms · ${esc(r.health?.mode ?? '')} mode${r.health?.version ? ` · v${esc(r.health.version)}` : ''}</span>` : `<span class="error">✕ ${esc(r.error)}</span>`;
      } catch (err) {
        out.innerHTML = `<span class="error">${esc(err.message)}</span>`;
      }
      return;
    }
    const ruleRow = t.closest('[data-rule]');
    if (t.closest('[data-rule-add]') || (ruleRow && t.closest('[data-rule-up],[data-rule-down],[data-rule-remove]'))) {
      const d = readRules();
      const i = ruleRow ? Number(ruleRow.dataset.rule) : -1;
      if (t.closest('[data-rule-add]')) d.rules.push({ match: '', folder: '' });
      else if (t.closest('[data-rule-remove]')) d.rules.splice(i, 1);
      else {
        const j = t.closest('[data-rule-up]') ? i - 1 : i + 1;
        [d.rules[i], d.rules[j]] = [d.rules[j], d.rules[i]];
      }
      redrawRules();
      return;
    }
    if (t.closest('[data-rules-reset]')) { rulesDraft = null; draw(); return; }
    if (t.closest('[data-rules-test]')) {
      const d = readRules();
      const out = $('#rules-test-out');
      out.hidden = false;
      try {
        const r = await api('/admin/photo-rules/test', { method: 'POST', body: { ...d, rules: d.rules.filter((x) => x.match.trim() || x.folder.trim()), job: $('#rules-test-job').value.trim(), load: $('#rules-test-load').value.trim() } });
        out.innerHTML = `${esc(r.loadFolder)}<br><span class="muted">${r.rule === 'default' ? 'No rule matched: default folder' : `Rule “${esc(r.rule)}” matched`}</span>`;
      } catch (err) {
        out.innerHTML = `<span class="error">${esc(err.message)}</span>`;
      }
      return;
    }
    if (t.closest('[data-rules-save]')) {
      const d = readRules();
      const out = $('#rules-saved');
      try {
        await api('/admin/photo-rules', { method: 'PUT', body: { ...d, rules: d.rules.filter((x) => x.match.trim() || x.folder.trim()) } });
        rulesDraft = null;
        await draw();
        $('#rules-saved').textContent = 'Saved. New photos use these folders now.';
      } catch (err) {
        out.textContent = err.message;
        out.className = 'error';
      }
    }
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
  $('#btn-lock').addEventListener('click', lockNow);

  const pin = $('#dlg-pin');
  pin.addEventListener('click', (e) => {
    const k = e.target.closest('[data-pin]');
    if (k) pressPin(k.dataset.pin);
  });
  pin.addEventListener('keydown', (e) => {
    if (!pad) return;
    if (/^\d$/.test(e.key)) pressPin(e.key);
    else if (e.key === 'Backspace') pressPin('back');
    else if (e.key === 'Enter') { if (pad.value.length >= 4) closePin(pad.value); }
    else return;
    e.preventDefault();
  });
  pin.addEventListener('cancel', (e) => { e.preventDefault(); closePin(null); });
  $('#pin-cancel').addEventListener('click', () => closePin(null));
  $('#pin-ok').addEventListener('click', () => closePin(pad?.value ?? null));

  // The lock button counts down; when the 30 minutes are up the board locks.
  setInterval(() => {
    const was = document.body.classList.contains('unlocked');
    drawLockButton();
    if (was && !unlockEntry()) { redrawReason = 'nav'; draw(); }
  }, 20000);

  // The Server page keeps itself up to date.
  setInterval(() => {
    if (currentPage === 'server' && adminToken() && !document.hidden && !$('#dlg-pin').open) { redrawReason = 'live'; draw(); }
  }, 10000);
  $('#dlg-photo').addEventListener('click', (e) => { if (e.target === e.currentTarget) e.currentTarget.close(); });
}

async function start() {
  drawLockButton();
  if ('serviceWorker' in navigator) navigator.serviceWorker.register('sw.js').catch(() => {});
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
