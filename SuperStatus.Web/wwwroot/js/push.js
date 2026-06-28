// Issue #241 Phase C: browser-side Web Push helpers, called from the Blazor
// "Enable notifications on this device" button (EnablePushButton.razor).
//
// Blazor owns the UI state + the server round-trips (fetch the VAPID key, POST the
// subscription); this module only does the things the circuit can't: register the
// service worker, prompt for permission, and create/read/remove the PushManager
// subscription. Everything returns plain JSON-serializable values for interop and
// is null/exception-safe so a refused permission or unsupported browser degrades
// to "not enabled" rather than throwing into the circuit.
window.superPush = (function () {
  // VAPID public keys travel as URL-safe base64; PushManager wants a Uint8Array.
  function urlBase64ToUint8Array(base64String) {
    const padding = '='.repeat((4 - (base64String.length % 4)) % 4);
    const base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
    const raw = atob(base64);
    const out = new Uint8Array(raw.length);
    for (let i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i);
    return out;
  }

  function isSupported() {
    return 'serviceWorker' in navigator &&
           'PushManager' in window &&
           'Notification' in window &&
           window.isSecureContext === true; // push requires HTTPS (or localhost)
  }

  function permission() {
    try { return Notification.permission; } catch (e) { return 'default'; }
  }

  function pack(sub) {
    if (!sub) return null;
    const json = sub.toJSON();
    const keys = json.keys || {};
    return { endpoint: sub.endpoint, p256dh: keys.p256dh, auth: keys.auth, userAgent: navigator.userAgent };
  }

  async function isSubscribed() {
    if (!isSupported()) return false;
    try {
      const reg = await navigator.serviceWorker.getRegistration();
      if (!reg) return false;
      const sub = await reg.pushManager.getSubscription();
      return !!sub;
    } catch (e) { return false; }
  }

  async function subscribe(vapidPublicKey) {
    if (!isSupported() || !vapidPublicKey) return null;
    try {
      const perm = await Notification.requestPermission();
      if (perm !== 'granted') return null;

      const reg = await navigator.serviceWorker.register('/service-worker.js');
      await navigator.serviceWorker.ready;

      let sub = await reg.pushManager.getSubscription();
      if (!sub) {
        sub = await reg.pushManager.subscribe({
          userVisibleOnly: true,
          applicationServerKey: urlBase64ToUint8Array(vapidPublicKey)
        });
      }
      return pack(sub);
    } catch (e) {
      return null;
    }
  }

  async function unsubscribe() {
    try {
      const reg = await navigator.serviceWorker.getRegistration();
      if (!reg) return null;
      const sub = await reg.pushManager.getSubscription();
      if (!sub) return null;
      const endpoint = sub.endpoint;
      await sub.unsubscribe();
      return endpoint;
    } catch (e) { return null; }
  }

  return { isSupported, permission, isSubscribed, subscribe, unsubscribe };
})();
