/* TERRA admin page service worker: lets the admin page be installed as an app (laptop, iPad). */
// The page's own files come from the server whenever it answers (so an update shows at once) and
// from this cache only when it doesn't. Nothing from /api is ever kept: the data, and the access
// key and admin code checks, always go to the server.

const CACHE = 'terra-admin-v1';
const SHELL = ['./', './index.html', './admin.css', './admin.js', './manifest.webmanifest', './icons/icon-192.png', './icons/icon-512.png'];

self.addEventListener('install', (event) => {
  event.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL.map((u) => new Request(u, { cache: 'reload' })))).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
  event.waitUntil(caches.keys()
    .then((keys) => Promise.all(keys.filter((k) => k.startsWith('terra-admin-') && k !== CACHE).map((k) => caches.delete(k))))
    .then(() => self.clients.claim()));
});

self.addEventListener('fetch', (event) => {
  const req = event.request;
  const url = new URL(req.url);
  if (req.method !== 'GET' || url.origin !== self.location.origin || !url.pathname.includes('/admin/')) return;
  event.respondWith(fetch(req, { cache: 'no-cache' })
    .then((res) => {
      if (res.ok) {
        const copy = res.clone();
        caches.open(CACHE).then((c) => c.put(req.mode === 'navigate' ? './index.html' : req, copy));
      }
      return res;
    })
    .catch(() => caches.match(req.mode === 'navigate' ? './index.html' : req, { ignoreSearch: true })
      .then((hit) => hit ?? Response.error())));
});
