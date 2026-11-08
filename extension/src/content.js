// Körs på stats.innebandy.se (även i dolda iframes). Sparar fångad API-data och kör,
// i toppfönstret, kön som laddar sidor i bakgrunden för att samla in kringliggande data.
(() => {
  const IS_TOP = window.top === window;

  // Pauser och tidsgränser för bakgrundsinsamlingen – en sida i taget, i lugn takt
  const PAUSE_MS = 1500;
  const PAGE_TIMEOUT_MS = 20000;
  const LINEUP_GRACE_MS = 3000;
  const MAX_FAILURES_IN_ROW = 4;
  const TEAM_MAX_AGE_MS = 7 * 24 * 60 * 60 * 1000;

  // Sätts nedan i toppfönstret: anropas när en matchlista fångats
  let onMatchList = null;

  // ---- Lagring av fångad data ----

  function storageItems(path, data) {
    const at = Date.now();
    let m;
    if ((m = /^competitions\/(\d+)\/matches$/.exec(path))) return { [`comp:${m[1]}`]: { at, matches: data } };
    if ((m = /^matches\/(\d+)$/.exec(path))) return { [`match:${m[1]}`]: { at, match: data } };
    if ((m = /^matches\/(\d+)\/lineups$/.exec(path))) return { [`lineup:${m[1]}`]: { at, lineup: data } };
    // Lag: bara en markering lokalt (truppen skickas till servern via synken)
    if ((m = /^seasons\/(\d+)\/teams\/(\d+)$/.exec(path))) {
      learnTeamRoute(+m[1], +m[2]);
      return { [`team:${m[2]}`]: { at } };
    }
    return null;
  }

  // Adressen till lagsidor på stats.innebandy.se lärs in första gången en lagsida öppnas:
  // t.ex. /sasong/44/lag/119228 blir mallen /sasong/{season}/lag/{team}
  function learnTeamRoute(seasonId, teamId) {
    const segments = location.pathname.split('/');
    if (!segments.includes(String(teamId))) return;
    const template = segments.map((segment, i) => {
      if (segment === String(teamId)) return '{team}';
      if (segment === String(seasonId)) return '{season}';
      if (segments[i - 1] === 'serie' && /^\d+$/.test(segment)) return '{competition}';
      return segment;
    }).join('/');
    chrome.storage.local.set({ 'route:team': template });
  }

  window.addEventListener('message', event => {
    if (event.source !== window || event.data?.source !== 'ibs-capture') return;
    let data;
    try {
      data = JSON.parse(event.data.body);
    } catch {
      return;
    }
    const items = storageItems(event.data.path, data);
    if (items) chrome.storage.local.set(items);

    // På en series spelprogram: hämta automatiskt alla spelade matcher som saknas
    const list = /^competitions\/(\d+)\/matches$/.exec(event.data.path);
    if (list && onMatchList) onMatchList(+list[1], data);

    // Allt som fångas skickas vidare till din egen server (om synk är påslagen)
    chrome.runtime.sendMessage({ type: 'ibs-sync', path: event.data.path, body: event.data.body }).catch(() => {});
  });

  if (!IS_TOP) return;

  // ---- Bakgrundsinsamling (bara i toppfönstret) ----

  const queue = [];
  const queued = new Set();
  let running = false;
  let mode = 'iframe';
  let failuresInRow = 0;
  let iframeFailures = 0;
  const status = { done: 0, failed: 0, total: 0, current: null, state: 'idle', mode };

  const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

  function publishStatus() {
    status.mode = mode;
    status.remaining = queue.length;
    const snapshot = { ...status, at: Date.now() };
    chrome.storage.local.set({ 'collector:status': snapshot });
    // Bakgrundsskriptet följer flikar som appen bett om att hämta
    chrome.runtime.sendMessage({ type: 'ibs-collector-status', status: snapshot }).catch(() => {});
  }

  // Väntar tills nyckeln skrivs (efter since) eller tidsgränsen passerar
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

  async function loadInIframe(job) {
    const since = Date.now();
    const frame = document.createElement('iframe');
    frame.setAttribute('aria-hidden', 'true');
    frame.tabIndex = -1;
    frame.style.cssText = 'position:fixed;left:-10000px;top:0;width:1200px;height:900px;border:0;visibility:hidden;';
    frame.src = job.path;
    document.documentElement.appendChild(frame);
    try {
      const ok = await waitForKey(job.key, since, PAGE_TIMEOUT_MS);
      if (ok && job.optionalKey) await waitForKey(job.optionalKey, since, LINEUP_GRACE_MS);
      return ok;
    } finally {
      frame.remove();
    }
  }

  async function loadInTab(job) {
    const response = await chrome.runtime.sendMessage({
      type: 'ibs-fetch-tab',
      url: new URL(job.path, location.origin).href,
      key: job.key,
      optionalKey: job.optionalKey,
      timeoutMs: PAGE_TIMEOUT_MS,
      graceMs: LINEUP_GRACE_MS
    });
    return Boolean(response?.ok);
  }

  async function run() {
    if (running) return;
    running = true;
    status.state = 'running';
    publishStatus();

    while (queue.length > 0) {
      const job = queue.shift();
      status.current = job.label;
      publishStatus();

      let ok = false;
      try {
        ok = mode === 'iframe' ? await loadInIframe(job) : await loadInTab(job);
      } catch {
        ok = false;
      }

      // Sajten kan neka inbäddning i iframe – byt då till bakgrundsflik
      if (!ok && mode === 'iframe' && ++iframeFailures >= 2 && status.done === 0) {
        mode = 'tab';
        try {
          ok = await loadInTab(job);
        } catch {
          ok = false;
        }
      }

      queued.delete(job.key);
      if (ok) {
        status.done++;
        failuresInRow = 0;
      } else {
        status.failed++;
        failuresInRow++;
      }
      publishStatus();

      if (failuresInRow >= MAX_FAILURES_IN_ROW) {
        status.state = 'error';
        status.current = null;
        queue.length = 0;
        queued.clear();
        publishStatus();
        running = false;
        return;
      }

      await sleep(PAUSE_MS);
    }

    status.state = 'idle';
    status.current = null;
    publishStatus();
    running = false;
  }

  function enqueueJobs(jobs) {
    for (const job of jobs) {
      if (queued.has(job.key)) continue;
      queued.add(job.key);
      if (job.priority) queue.unshift(job);
      else queue.push(job);
      status.total++;
    }
    run();
    publishStatus();
  }

  const isPlayed = m => m.MatchStatus === 4 && m.GoalsHomeTeam != null && m.GoalsAwayTeam != null;

  // Spelprogrammet (/sasong/{s}/serie/{c}/spelprogram…) laddar seriens matchlista.
  // Köa de spelade matcher vars detaljer saknas eller sparades innan matchen var färdigspelad.
  onMatchList = async (competitionId, matches) => {
    const page = /\/sasong\/(\d+)\/serie\/(\d+)\/spelprogram/.exec(location.pathname);
    if (!page || +page[2] !== competitionId || !Array.isArray(matches) || status.state === 'error') return;
    const seasonId = +page[1];

    const played = matches.filter(isPlayed);
    const stored = await chrome.storage.local.get(played.map(m => `match:${m.MatchID}`));
    const missing = played
      .filter(m => !stored[`match:${m.MatchID}`]?.match || !isPlayed(stored[`match:${m.MatchID}`].match))
      .sort((a, b) => new Date(b.MatchDateTime) - new Date(a.MatchDateTime));

    const jobs = missing.map(m => ({
      key: `match:${m.MatchID}`,
      optionalKey: `lineup:${m.MatchID}`,
      path: `/sasong/${seasonId}/serie/${competitionId}/match/${m.MatchID}`,
      label: `${m.HomeTeam.trim()} – ${m.AwayTeam.trim()}`
    }));

    // Lagens trupper (ålder och födelseår) – om lagsidans adress är inlärd, högst en gång i veckan per lag
    const { 'route:team': teamRoute } = await chrome.storage.local.get('route:team');
    if (teamRoute) {
      const teams = new Map();
      for (const m of matches) {
        if (m.HomeTeamID) teams.set(m.HomeTeamID, m.HomeTeam.trim());
        if (m.AwayTeamID) teams.set(m.AwayTeamID, m.AwayTeam.trim());
      }
      const teamKeys = [...teams.keys()].map(id => `team:${id}`);
      const storedTeams = await chrome.storage.local.get(teamKeys);
      for (const [teamId, name] of teams) {
        if (Date.now() - (storedTeams[`team:${teamId}`]?.at ?? 0) < TEAM_MAX_AGE_MS) continue;
        jobs.push({
          key: `team:${teamId}`,
          path: teamRoute.replace('{season}', seasonId).replace('{team}', teamId).replace('{competition}', competitionId),
          label: `Lag: ${name}`
        });
      }
    }

    enqueueJobs(jobs);
  };

  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (message?.type !== 'ibs-collect') return;

    if (status.state === 'error' && message.retry) {
      status.state = 'idle';
      failuresInRow = 0;
    }
    // Prioriterade jobb (t.ex. matchlistan) läggs först
    if (status.state !== 'error') enqueueJobs(message.jobs ?? []);
    else publishStatus();
    sendResponse({ ok: true, state: status.state });
  });
})();
