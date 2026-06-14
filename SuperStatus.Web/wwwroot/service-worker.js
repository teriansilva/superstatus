// Issue #241 Phase C: SuperStatus push service worker.
//
// This worker exists ONLY to receive Web Push and surface notifications — there is
// no offline/asset caching (the app is a live Blazor Server circuit). It is
// registered lazily by js/push.js the first time an operator enables notifications
// on a device, so no worker is installed for visitors who never opt in.
//
// Payload shape is produced by SuperStatus.Services WebPushNotifier:
//   { title, body, url, tag }

self.addEventListener('push', function (event) {
  let data = {};
  try {
    data = event.data ? event.data.json() : {};
  } catch (e) {
    // Non-JSON payload — fall back to the raw text as the body.
    data = { title: 'SuperStatus', body: event.data ? event.data.text() : '' };
  }

  const title = data.title || 'SuperStatus';
  const options = {
    body: data.body || '',
    tag: data.tag,                 // collapses repeat alerts for the same check
    renotify: !!data.tag,
    data: { url: data.url || '/admin' },
    icon: '/_content/SuperStatus.Ui.Shared/favicon.png',
    badge: '/_content/SuperStatus.Ui.Shared/favicon.png'
  };
  event.waitUntil(self.registration.showNotification(title, options));
});

self.addEventListener('notificationclick', function (event) {
  event.notification.close();
  const url = (event.notification.data && event.notification.data.url) || '/admin';
  event.waitUntil(
    self.clients.matchAll({ type: 'window', includeUncontrolled: true }).then(function (list) {
      for (const client of list) {
        // Focus an existing tab already on the target URL.
        if (client.url.indexOf(url) !== -1 && 'focus' in client) return client.focus();
      }
      if (self.clients.openWindow) return self.clients.openWindow(url);
    })
  );
});

// Activate immediately so the first opt-in works without a reload.
self.addEventListener('install', function () { self.skipWaiting(); });
self.addEventListener('activate', function (event) { event.waitUntil(self.clients.claim()); });
