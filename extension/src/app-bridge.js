// Körs på Innebandystats-appens sidor. Berättar för sidan att pluginet finns och förmedlar
// "hämta den här sidan på stats.innebandy.se" från appen till pluginets bakgrundsskript.
(() => {
  document.documentElement.dataset.ibsPlugin = chrome.runtime.getManifest().version;

  window.addEventListener('message', event => {
    if (event.source !== window || event.data?.source !== 'ibs-app') return;

    if (event.data.type === 'clear') {
      const competitionId = Number(event.data.competitionId);
      if (!Number.isInteger(competitionId)) return;
      chrome.runtime.sendMessage({ type: 'ibs-app-clear', competitionId })
        .then(result => window.postMessage({ source: 'ibs-plugin', type: 'cleared', ...result }, location.origin))
        .catch(error => window.postMessage({ source: 'ibs-plugin', type: 'cleared', ok: false, error: String(error?.message ?? error) }, location.origin));
      return;
    }

    if (event.data.type !== 'fetch') return;
    const url = String(event.data.url ?? '');
    if (!url.startsWith('https://stats.innebandy.se/')) return;

    chrome.runtime.sendMessage({ type: 'ibs-app-fetch', url, appOrigin: location.origin })
      .then(result => window.postMessage({ source: 'ibs-plugin', type: 'done', ...result }, location.origin))
      .catch(error => window.postMessage({ source: 'ibs-plugin', type: 'done', ok: false, error: String(error?.message ?? error) }, location.origin));
  });

  // Förlopp från bakgrundsskriptet
  chrome.runtime.onMessage.addListener(message => {
    if (message?.type === 'ibs-app-progress') {
      window.postMessage({ source: 'ibs-plugin', type: 'progress', status: message.status }, location.origin);
    }
  });
})();
