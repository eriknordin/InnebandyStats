// Körs i sidans egen JavaScript-värld. Fångar svaren som stats.innebandy.se själv
// hämtar från api.innebandy.se och skickar dem vidare till content.js.
(() => {
  const API = /^https:\/\/api\.innebandy\.se\/v2\/api\/public\/([^?#]+)/;

  const post = (url, status, body) => {
    if (status !== 200 || typeof body !== 'string') return;
    const match = API.exec(new URL(url, location.href).href);
    if (!match) return;
    window.postMessage({ source: 'ibs-capture', path: match[1].replace(/\/+$/, ''), body }, location.origin);
  };

  const xhr = XMLHttpRequest.prototype;
  const open = xhr.open;
  const send = xhr.send;

  xhr.open = function (method, url) {
    this.__ibsUrl = String(method).toUpperCase() === 'GET' ? String(url) : null;
    return open.apply(this, arguments);
  };

  xhr.send = function () {
    if (this.__ibsUrl) {
      this.addEventListener('load', () => {
        try {
          const body = this.responseType === '' || this.responseType === 'text'
            ? this.responseText
            : JSON.stringify(this.response);
          post(this.responseURL || this.__ibsUrl, this.status, body);
        } catch {
          // Fångsten får aldrig störa sidan
        }
      });
    }
    return send.apply(this, arguments);
  };

  const originalFetch = window.fetch;
  window.fetch = async function (input, init) {
    const response = await originalFetch.apply(this, arguments);
    try {
      const url = typeof input === 'string' ? input : input.url;
      const method = (init && init.method) || (typeof input === 'object' && input.method) || 'GET';
      if (method.toUpperCase() === 'GET' && API.test(new URL(url, location.href).href)) {
        response.clone().text().then(text => post(response.url || url, response.status, text)).catch(() => {});
      }
    } catch {
      // Fångsten får aldrig störa sidan
    }
    return response;
  };
})();
