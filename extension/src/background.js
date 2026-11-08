import { enqueue, enqueueAllCollected, flush, getSettings, getStatus, removeFromOutbox, resetCounters, scheduleFlush, updateBadge } from './sync.js';

// Öppna sidopanelen när man klickar på pluginets ikon
chrome.sidePanel.setPanelBehavior({ openPanelOnActionClick: true }).catch(() => {});

// Försök skicka utkorgen igen med jämna mellanrum (om servern varit nere)
chrome.alarms.create('ibs-sync-retry', { periodInMinutes: 5 });
chrome.alarms.onAlarm.addListener(alarm => {
  if (alarm.name === 'ibs-sync-retry') flush().catch(() => {});
});
chrome.runtime.onStartup.addListener(() => scheduleFlush(5000));

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === 'ibs-app-fetch') {
    fetchForApp(message, sender.tab?.id).then(sendResponse, error => sendResponse({ ok: false, error: String(error?.message ?? error) }));
    return true;
  }

  if (message?.type === 'ibs-app-clear') {
    clearCompetition(message.competitionId).then(sendResponse, error => sendResponse({ ok: false, error: String(error?.message ?? error) }));
    return true;
  }

  if (message?.type === 'ibs-collector-status') {
    const fetch = sender.tab && appFetches.get(sender.tab.id);
    if (fetch) fetch.onStatus(message.status);
    return;
  }

  if (message?.type === 'ibs-fetch-tab') {
    // Reserv när sajten inte går att ladda i en iframe
    fetchViaTab(message).then(ok => sendResponse({ ok }), () => sendResponse({ ok: false }));
    return true;
  }

  if (message?.type === 'ibs-sync-all') {
    enqueueAllCollected().then(count => sendResponse({ count }), () => sendResponse({ count: 0 }));
    return true;
  }

  if (message?.type === 'ibs-sync-flush') {
    updateBadge().catch(() => {});
    flush().catch(() => {});
    return;
  }

  if (message?.type === 'ibs-sync-reset') {
    resetCounters().then(() => sendResponse({ ok: true }), () => sendResponse({ ok: false }));
    return true;
  }

  if (message?.type === 'ibs-sync') {
    let data;
    try {
      data = JSON.parse(message.body);
    } catch {
      return;
    }
    enqueue([{ path: message.path, data: stripSensitive(data) }]).catch(() => {});
  }
});

// Kontaktuppgifter (ledares e-post, telefon, personnummer) följer med i vissa svar, t.ex. lag.
// De behövs inte och skickas aldrig till servern.
const SENSITIVE = new Set(['contactperson', 'teampersons', 'email', 'mobilephone', 'homephone', 'workphone', 'personalidentitynumber']);
function stripSensitive(value) {
  if (Array.isArray(value)) return value.map(stripSensitive);
  if (value && typeof value === 'object') {
    const clean = {};
    for (const [key, child] of Object.entries(value)) {
      if (!SENSITIVE.has(key.toLowerCase())) clean[key] = stripSensitive(child);
    }
    return clean;
  }
  return value;
}

// ---- Seriens lokalt sparade data ----

// Lagringsnycklar och API-sökvägar för en series matchlista, matcher och laguppställningar
async function competitionEntries(competitionId) {
  const listKey = `comp:${competitionId}`;
  const { [listKey]: list } = await chrome.storage.local.get(listKey);
  const matchIds = (list?.matches ?? []).map(m => m.MatchID);
  return {
    list,
    keys: [listKey, ...matchIds.flatMap(id => [`match:${id}`, `lineup:${id}`])],
    paths: [`competitions/${competitionId}/matches`, ...matchIds.flatMap(id => [`matches/${id}`, `matches/${id}/lineups`])]
  };
}

// Rensar pluginets egen kopia av serien (och sådant som väntar på att skickas), så att
// nästa synk hämtar allt på nytt från stats.innebandy.se
async function clearCompetition(competitionId) {
  const { keys, paths } = await competitionEntries(competitionId);
  await chrome.storage.local.remove(keys);
  await removeFromOutbox(paths);
  return { ok: true, removed: keys.length };
}

// Skickar seriens lokalt sparade matcher till servern igen, så att den blir komplett även
// om den rensats (t.ex. från mobilen) medan pluginet redan hade matcherna
async function resendCompetition(competitionId) {
  const { keys, paths } = await competitionEntries(competitionId);
  const stored = await chrome.storage.local.get(keys);
  const documents = [];
  keys.forEach((key, i) => {
    const value = stored[key];
    const data = value?.matches ?? value?.match ?? value?.lineup;
    if (data) documents.push({ path: paths[i], data });
  });
  await enqueue(documents);
}

// ---- Hämtning på begäran från appen ----

// Flikar som öppnats åt appen: tabId -> { onStatus }
const appFetches = new Map();
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const SERIES_TIMEOUT_MS = 15 * 60 * 1000;

// Öppnar sidan på stats.innebandy.se i en bakgrundsflik, väntar tills datan fångats
// (för ett spelprogram: tills alla spelade matcher hämtats), skickar allt till servern
// och stänger fliken. Förloppet skickas till appens flik.
async function fetchForApp({ url, appOrigin }, appTabId) {
  if (!url.startsWith('https://stats.innebandy.se/')) return { ok: false, error: 'Ogiltig adress.' };

  const settings = await getSettings();
  if (!settings.enabled) return { ok: false, error: 'Synk är avslagen i pluginets inställningar.' };
  const syncOrigin = new URL(settings.url).origin;
  if (appOrigin && syncOrigin !== appOrigin) {
    return { ok: false, error: `Pluginet synkar till ${syncOrigin}, inte till den här appen (${appOrigin}). Ändra adressen i pluginets inställningar.` };
  }

  const progress = status => {
    if (appTabId != null) chrome.tabs.sendMessage(appTabId, { type: 'ibs-app-progress', status }).catch(() => {});
  };

  const path = new URL(url).pathname;
  const series = /\/sasong\/\d+\/serie\/(\d+)\/spelprogram/.exec(path);
  const match = /\/match\/(\d+)/.exec(path);

  let lastStatus = null;
  let statusWaiter = null;
  const since = Date.now();
  const tab = await chrome.tabs.create({ url, active: false });
  appFetches.set(tab.id, {
    onStatus: status => {
      lastStatus = status;
      progress({ phase: 'collect', ...status });
      statusWaiter?.(status);
    }
  });

  let collected = true;
  try {
    progress({ phase: 'open' });
    if (series) {
      collected = await waitForKey(`comp:${series[1]}`, since, 30000);
      // Vänta tills insamlingskön i fliken är tom (eller stoppad)
      const done = s => s && s.state !== 'running' && !s.remaining;
      const deadline = Date.now() + SERIES_TIMEOUT_MS;
      if (collected && !done(lastStatus)) {
        await new Promise(resolve => {
          const timer = setTimeout(resolve, Math.max(0, deadline - Date.now()));
          // Inget att hämta: spelprogrammet rapporterar då ingen kö alls
          const quiet = setTimeout(() => { if (!lastStatus) { clearTimeout(timer); resolve(); } }, 15000);
          statusWaiter = s => {
            if (done(s)) {
              clearTimeout(timer);
              clearTimeout(quiet);
              resolve();
            }
          };
        });
      }
    } else if (match) {
      collected = await waitForKey(`match:${match[1]}`, since, 30000);
      if (collected) await waitForKey(`lineup:${match[1]}`, since, 3000);
    } else {
      // Övriga sidor (förbund, säsonger m.m.): ge sidan en stund att hämta sin data
      await sleep(8000);
    }
  } finally {
    appFetches.delete(tab.id);
    chrome.tabs.remove(tab.id).catch(() => {});
  }

  // Se till att allt som fångats har nått servern innan appen laddar om
  progress({ phase: 'sync' });
  if (series && collected) await resendCompetition(+series[1]);
  const syncDeadline = Date.now() + 60000;
  let syncStatus = await getStatus();
  while (Date.now() < syncDeadline) {
    await sleep(1500);
    await flush().catch(() => {});
    syncStatus = await getStatus();
    if (!syncStatus.pending || syncStatus.lastError) break;
  }

  if (syncStatus.lastError) return { ok: false, error: `Datan hämtades men kunde inte skickas: ${syncStatus.lastError}` };
  if (!collected) return { ok: false, error: 'Sidan på stats.innebandy.se laddade ingen data. Prova att öppna den själv.' };
  return {
    ok: true,
    fetched: lastStatus?.done ?? 0,
    failed: lastStatus?.failed ?? 0,
    pending: syncStatus.pending ?? 0
  };
}

function waitForKey(key, since, timeoutMs) {
  return new Promise(resolve => {
    let finished = false;
    const finish = ok => {
      if (finished) return;
      finished = true;
      chrome.storage.onChanged.removeListener(listener);
      clearTimeout(timer);
      resolve(ok);
    };
    const listener = (changes, area) => {
      if (area === 'local' && changes[key] && (changes[key].newValue?.at ?? 0) >= since) finish(true);
    };
    const timer = setTimeout(() => finish(false), timeoutMs);
    chrome.storage.onChanged.addListener(listener);
  });
}

async function fetchViaTab({ url, key, optionalKey, timeoutMs, graceMs }) {
  if (!url.startsWith('https://stats.innebandy.se/')) return false;

  const since = Date.now();
  const tab = await chrome.tabs.create({ url, active: false });
  try {
    const ok = await waitForKey(key, since, timeoutMs);
    if (ok && optionalKey) await waitForKey(optionalKey, since, graceMs);
    return ok;
  } finally {
    chrome.tabs.remove(tab.id).catch(() => {});
  }
}
