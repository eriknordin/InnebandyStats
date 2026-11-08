import { SETTINGS_KEY, DEFAULT_API_KEY, getSettings, getStatus, postDocuments } from './sync.js';

const $ = id => document.getElementById(id);
const message = $('message');

function show(text, kind = '') {
  message.textContent = text;
  message.className = kind;
}

function readForm() {
  return {
    enabled: $('enabled').checked,
    url: $('url').value.trim(),
    apiKey: $('apiKey').value.trim()
  };
}

function originPattern(url) {
  const u = new URL(url);
  return `${u.protocol}//${u.host}/*`;
}

async function load() {
  const settings = await getSettings();
  $('enabled').checked = settings.enabled;
  $('url').value = settings.url;
  // Visa bara en egen nyckel – standardnyckeln syns som platshållare
  $('apiKey').value = settings.apiKey === DEFAULT_API_KEY ? '' : settings.apiKey;
  renderStatus();
}

const KIND_LABELS = { matchlist: 'Matchlistor', match: 'Matcher', lineup: 'Laguppställningar', player: 'Spelare', other: 'Övrigt (säsonger, förbund, serier m.m.)' };

async function renderStatus() {
  const status = await getStatus();
  const time = t => new Date(t).toLocaleString('sv-SE');
  const parts = [
    `Väntar på att skickas: ${status.pending}`,
    `Skickade totalt: ${status.sentTotal}${status.countersResetAt ? ` (sedan ${time(status.countersResetAt)})` : ''}`
  ];
  for (const [kind, label] of Object.entries(KIND_LABELS)) {
    if (status.sentByKind[kind]) parts.push(`  · ${label}: ${status.sentByKind[kind]}`);
  }
  if (status.rejectedTotal) parts.push(`Avvisade av servern: ${status.rejectedTotal}`);
  if (status.lastSuccessAt) parts.push(`Senast skickat: ${time(status.lastSuccessAt)}`);
  if (status.lastError) parts.push(`Senaste fel (${time(status.lastErrorAt)}): ${status.lastError}`);
  $('status').innerHTML = parts.map(p => `<div>${p.replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' })[c])}</div>`).join('');
}

$('resetCounters').addEventListener('click', async () => {
  await chrome.runtime.sendMessage({ type: 'ibs-sync-reset' });
  show('Räknarna är nollställda.', 'ok');
});

$('save').addEventListener('click', () => {
  const settings = readForm();
  let pattern = null;
  if (settings.url) {
    try {
      pattern = originPattern(settings.url);
    } catch {
      show('Ogiltig adress.', 'error');
      return;
    }
  }
  if (settings.enabled && !settings.url) {
    show('Fyll i serverns adress för att slå på synk.', 'error');
    return;
  }

  // Behörigheten måste begäras direkt i klicket
  const permission = pattern ? chrome.permissions.request({ origins: [pattern] }) : Promise.resolve(true);
  permission.then(async granted => {
    if (!granted) {
      show('Pluginet fick inte behörighet att kontakta servern.', 'error');
      return;
    }
    await chrome.storage.local.set({ [SETTINGS_KEY]: settings });
    show('Sparat.', 'ok');
    // Skicka det som väntar och uppdatera ikonen (töms när synk slås av)
    chrome.runtime.sendMessage({ type: 'ibs-sync-flush' }).catch(() => {});
  });
});

$('test').addEventListener('click', async () => {
  const settings = readForm();
  if (!settings.url) {
    show('Fyll i serverns adress först.', 'error');
    return;
  }
  settings.apiKey ||= DEFAULT_API_KEY;
  show('Testar…');
  try {
    if (!(await chrome.permissions.contains({ origins: [originPattern(settings.url)] }))) {
      show('Klicka Spara först så att pluginet får behörighet att kontakta servern.', 'error');
      return;
    }
    await postDocuments(settings, []);
    show('Anslutningen fungerar.', 'ok');
  } catch (error) {
    show(`Fel: ${error.message ?? error}`, 'error');
  }
});

$('sendAll').addEventListener('click', async () => {
  const settings = await getSettings();
  if (!settings.enabled) {
    show('Slå på och spara synk först.', 'error');
    return;
  }
  const response = await chrome.runtime.sendMessage({ type: 'ibs-sync-all' });
  show(`${response?.count ?? 0} dokument lagda i kö för att skickas.`, 'ok');
});

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'local' && changes['sync:status']) renderStatus();
});

load();
