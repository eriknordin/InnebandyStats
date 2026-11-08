// Skickar fångade API-dokument till din egen Innebandystats-server (POST /api/ingest).
// Dokument läggs först i en utkorg i chrome.storage.local och tas bort först när servern
// har tagit emot dem – så inget tappas om servern är nere eller service workern somnar.

export const SETTINGS_KEY = 'sync:settings';
// Samma nyckel som Ingest:ApiKey i appens appsettings.json
export const DEFAULT_API_KEY = 'ibs-p-Epz7tADw6R_xZWvx6e-uOsFo3bY7V8';
export const DEFAULT_URL = 'https://innebandy.azurewebsites.net/';
const OUTBOX_KEY = 'sync:outbox';
const STATUS_KEY = 'sync:status';
const BATCH_SIZE = 100;
const FLUSH_DELAY_MS = 2000;

let flushTimer = null;
let flushing = false;

// Utkorgen läses och skrivs i flera steg – kör ändringarna en i taget så att inget skrivs över
let outboxChain = Promise.resolve();
const withOutbox = fn => (outboxChain = outboxChain.then(fn, fn));

export async function getSettings() {
  const result = await chrome.storage.local.get(SETTINGS_KEY);
  // Utan sparade inställningar är synk påslagen mot standardservern
  const settings = { enabled: true, url: DEFAULT_URL, ...(result[SETTINGS_KEY] ?? {}) };
  // Tom adress/nyckel = standardvärdena
  return { ...settings, url: settings.url || DEFAULT_URL, apiKey: settings.apiKey || DEFAULT_API_KEY };
}

// Dokumenttyp ur API-sökvägen, för räknarna per typ
export function kindOf(path) {
  if (/^competitions\/\d+\/matches$/.test(path)) return 'matchlist';
  if (/^matches\/\d+$/.test(path)) return 'match';
  if (/^matches\/\d+\/lineups$/.test(path)) return 'lineup';
  if (/^players\/\d+$/.test(path)) return 'player';
  return 'other';
}

export async function getStatus() {
  const result = await chrome.storage.local.get(STATUS_KEY);
  return { pending: 0, sentTotal: 0, sentByKind: {}, rejectedTotal: 0, ...(result[STATUS_KEY] ?? {}) };
}

// Status läses och skrivs i flera steg – en uppdatering i taget
let statusChain = Promise.resolve();
function updateStatus(update) {
  statusChain = statusChain.then(async () => {
    const current = await getStatus();
    const next = { ...current, ...update(current) };
    await chrome.storage.local.set({ [STATUS_KEY]: next });
    await updateBadge(next);
  }).catch(() => {});
  return statusChain;
}
const setStatus = patch => updateStatus(() => patch);

export function resetCounters() {
  return updateStatus(() => ({ sentTotal: 0, sentByKind: {}, rejectedTotal: 0, countersResetAt: Date.now() }));
}

// Ikonen: rött ! vid fel, annars antal som väntar (tomt när allt är skickat eller synk är av)
export async function updateBadge(status) {
  const settings = await getSettings();
  status ??= await getStatus();
  if (!settings.enabled) {
    await chrome.action.setBadgeText({ text: '' });
  } else if (status.lastError) {
    await chrome.action.setBadgeText({ text: '!' });
    await chrome.action.setBadgeBackgroundColor({ color: '#b42318' });
  } else if (status.pending > 0) {
    await chrome.action.setBadgeText({ text: status.pending > 999 ? '999+' : String(status.pending) });
    await chrome.action.setBadgeBackgroundColor({ color: '#0b5cad' });
  } else {
    await chrome.action.setBadgeText({ text: '' });
  }
}

// Lägger dokument i utkorgen. documents: [{ path, data }]
export async function enqueue(documents) {
  const settings = await getSettings();
  if (!settings.enabled || documents.length === 0) return;

  await withOutbox(async () => {
    const result = await chrome.storage.local.get(OUTBOX_KEY);
    const outbox = result[OUTBOX_KEY] ?? {};
    for (const doc of documents) outbox[doc.path] = doc.data;
    await chrome.storage.local.set({ [OUTBOX_KEY]: outbox });
    await setStatus({ pending: Object.keys(outbox).length });
  });
  scheduleFlush();
}

// Tar bort dokument ur utkorgen (t.ex. när en serie rensas)
export async function removeFromOutbox(paths) {
  await withOutbox(async () => {
    const outbox = (await chrome.storage.local.get(OUTBOX_KEY))[OUTBOX_KEY] ?? {};
    for (const path of paths) delete outbox[path];
    await chrome.storage.local.set({ [OUTBOX_KEY]: outbox });
    await setStatus({ pending: Object.keys(outbox).length });
  });
}

export function scheduleFlush(delay = FLUSH_DELAY_MS) {
  clearTimeout(flushTimer);
  flushTimer = setTimeout(() => flush().catch(() => {}), delay);
}

function ingestUrl(base) {
  return new URL('api/ingest', base.endsWith('/') ? base : `${base}/`).href;
}

export async function postDocuments(settings, documents) {
  const response = await fetch(ingestUrl(settings.url), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Api-Key': settings.apiKey },
    body: JSON.stringify({ documents })
  });
  if (!response.ok) {
    const text = await response.text().catch(() => '');
    throw new Error(`Servern svarade ${response.status}${text ? `: ${text.slice(0, 200)}` : ''}`);
  }
  return response.json();
}

export async function flush() {
  if (flushing) return;
  flushing = true;
  try {
    const settings = await getSettings();
    if (!settings.enabled || !settings.url || !settings.apiKey) return;

    let outbox = (await chrome.storage.local.get(OUTBOX_KEY))[OUTBOX_KEY] ?? {};

    while (Object.keys(outbox).length > 0) {
      const batch = Object.entries(outbox).slice(0, BATCH_SIZE).map(([path, data]) => ({ path, data }));
      let response;
      try {
        response = await postDocuments(settings, batch);
      } catch (error) {
        await setStatus({ lastError: String(error.message ?? error), lastErrorAt: Date.now(), pending: Object.keys(outbox).length });
        return;
      }

      // Räkna det servern tog emot (avvisade sökvägar räknas för sig)
      const rejected = new Set(response?.rejected ?? []);
      const delivered = batch.filter(doc => !rejected.has(doc.path));
      await updateStatus(status => {
        const byKind = { ...status.sentByKind };
        for (const doc of delivered) byKind[kindOf(doc.path)] = (byKind[kindOf(doc.path)] ?? 0) + 1;
        return {
          sentTotal: status.sentTotal + delivered.length,
          sentByKind: byKind,
          rejectedTotal: status.rejectedTotal + rejected.size
        };
      });

      // Ta bort det som skickats – men inte dokument som hunnit uppdateras under tiden
      outbox = await withOutbox(async () => {
        const current = (await chrome.storage.local.get(OUTBOX_KEY))[OUTBOX_KEY] ?? {};
        for (const doc of batch) {
          if (JSON.stringify(current[doc.path]) === JSON.stringify(doc.data)) delete current[doc.path];
        }
        await chrome.storage.local.set({ [OUTBOX_KEY]: current });
        await setStatus({ lastSuccessAt: Date.now(), lastError: null, pending: Object.keys(current).length });
        return current;
      });
    }
  } finally {
    flushing = false;
  }
}

// Lägger allt pluginet redan har samlat in i utkorgen (t.ex. när synk slås på första gången)
export async function enqueueAllCollected() {
  const all = await chrome.storage.local.get(null);
  const documents = [];
  for (const [key, value] of Object.entries(all)) {
    let m;
    if ((m = /^comp:(\d+)$/.exec(key)) && value?.matches) documents.push({ path: `competitions/${m[1]}/matches`, data: value.matches });
    else if ((m = /^match:(\d+)$/.exec(key)) && value?.match) documents.push({ path: `matches/${m[1]}`, data: value.match });
    else if ((m = /^lineup:(\d+)$/.exec(key)) && value?.lineup) documents.push({ path: `matches/${m[1]}/lineups`, data: value.lineup });
  }
  await enqueue(documents);
  return documents.length;
}
