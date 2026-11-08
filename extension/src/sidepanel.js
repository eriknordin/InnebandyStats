import {
  buildTable, positionHistory, playerStandings, goalieStandings, buildPlayerLines,
  isPlayed, isLive, isUpcoming, matchTime, isGoalEvent
} from './lib/stats.js';
import {
  getCompetition, getCollectorStatus, getMatchDetails, missingDetails, sitePaths, MATCH_LIST_MAX_AGE_MS
} from './lib/store.js';

const app = document.getElementById('app');
const titleEl = document.getElementById('title');
const backButton = document.getElementById('back');
const collectorEl = document.getElementById('collector');

const state = {
  tabId: null,
  tabUrl: null,
  route: null,
  stack: [],
  seriesTab: 'table',
  tableScope: 'all',
  standingsSort: { key: 'points', desc: true },
  standingsTeam: '',
  contentMissing: false
};

// ---- Hjälpfunktioner ----

const h = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const fmtDay = new Intl.DateTimeFormat('sv-SE', { weekday: 'short', day: 'numeric', month: 'short' });
const fmtTime = new Intl.DateTimeFormat('sv-SE', { hour: '2-digit', minute: '2-digit' });
const fmtDate = new Intl.DateTimeFormat('sv-SE', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' });
const pct = v => v == null ? '–' : `${v.toFixed(1)} %`;
const signed = v => v > 0 ? `+${v}` : String(v);
const clock = (m, s) => `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
const minutes = seconds => `${Math.floor(seconds / 60)}:${String(Math.round(seconds % 60)).padStart(2, '0')}`;

function parseContext(url) {
  let u;
  try {
    u = new URL(url);
  } catch {
    return null;
  }
  if (u.hostname !== 'stats.innebandy.se') return null;

  let m = /\/sasong\/(\d+)\/serie\/(\d+)(?:\/match\/(\d+))?/.exec(u.pathname);
  if (m) return { seasonId: +m[1], competitionId: +m[2], matchId: m[3] ? +m[3] : null };
  m = /\/match\/(\d+)/.exec(u.pathname);
  if (m) return { matchId: +m[1] };
  return {};
}

function rootRouteFor(context) {
  if (!context) return null;
  if (context.matchId) return { view: 'match', ...context };
  if (context.competitionId) return { view: 'series', ...context };
  return { view: 'none' };
}

// ---- Bakgrundsinsamling ----

const requested = new Map();
const REQUEST_AGAIN_MS = 5 * 60 * 1000;

async function collect(jobs, { retry = false } = {}) {
  if (!state.tabId) return;
  const fresh = jobs.filter(j => retry || !requested.has(j.key) || Date.now() - requested.get(j.key) > REQUEST_AGAIN_MS);
  if (fresh.length === 0 && !retry) return;
  fresh.forEach(j => requested.set(j.key, Date.now()));
  try {
    await chrome.tabs.sendMessage(state.tabId, { type: 'ibs-collect', jobs: fresh, retry });
    state.contentMissing = false;
  } catch {
    // Fliken öppnades innan pluginet installerades/laddades om
    fresh.forEach(j => requested.delete(j.key));
    state.contentMissing = true;
    renderCollector(await getCollectorStatus());
  }
}

function matchListJob(seasonId, competitionId) {
  return { key: `comp:${competitionId}`, path: sitePaths.matchList(seasonId, competitionId), label: 'Matchlista', priority: true };
}

function matchJob(seasonId, competitionId, m) {
  return {
    key: `match:${m.MatchID}`,
    optionalKey: `lineup:${m.MatchID}`,
    path: sitePaths.match(seasonId, competitionId, m.MatchID),
    label: `${m.HomeTeam.trim()} – ${m.AwayTeam.trim()}`
  };
}

// Laddar seriens data och ber om det som saknas
async function loadSeries(route) {
  const comp = await getCompetition(route.competitionId);
  if (!comp) {
    if (route.seasonId) collect([matchListJob(route.seasonId, route.competitionId)]);
    return null;
  }
  if (route.seasonId && Date.now() - comp.at > MATCH_LIST_MAX_AGE_MS) {
    collect([matchListJob(route.seasonId, route.competitionId)]);
  }

  const matches = comp.matches ?? [];
  const playedIds = matches.filter(isPlayed).map(m => m.MatchID);
  const details = await getMatchDetails(playedIds);
  const missing = missingDetails(matches, details);
  if (route.seasonId && missing.length > 0) {
    // Nyaste först, så att aktuell form fylls på snabbast
    const ordered = [...missing].sort((a, b) => matchTime(b) - matchTime(a));
    collect(ordered.map(m => matchJob(route.seasonId, route.competitionId, m)));
  }

  const played = playedIds.map(id => details.get(id)).filter(Boolean);
  return {
    matches,
    details,
    played,
    name: (matches[0]?.CompetitionName ?? '').trim(),
    playedCount: playedIds.length
  };
}

// ---- Rendering ----

function renderCollector(status) {
  if (state.contentMissing) {
    collectorEl.hidden = false;
    collectorEl.className = 'error';
    collectorEl.textContent = 'Ladda om fliken med stats.innebandy.se så att pluginet kan samla in data.';
    return;
  }
  if (!status || (status.state === 'idle' && !status.remaining)) {
    collectorEl.hidden = true;
    return;
  }
  collectorEl.hidden = false;
  if (status.state === 'error') {
    collectorEl.className = 'error';
    collectorEl.innerHTML = `Insamlingen stoppades efter flera misslyckade sidladdningar. <span class="link" data-action="retry">Försök igen</span>`;
    return;
  }
  collectorEl.className = '';
  const total = status.done + status.failed + (status.remaining ?? 0);
  collectorEl.innerHTML = `Hämtar ${h(status.current ?? '')} – ${status.done + status.failed} av ${total}${status.failed ? ` (${status.failed} misslyckade)` : ''}${status.mode === 'tab' ? ' · via bakgrundsflik' : ''}
    <progress max="${total}" value="${status.done + status.failed}"></progress>`;
}

function formBadges(form) {
  return `<span class="form">${form.map(r => `<span class="${r}">${r}</span>`).join('')}</span>`;
}

function scoreText(m) {
  if (isPlayed(m)) return `${m.GoalsHomeTeam}–${m.GoalsAwayTeam}`;
  if (isLive(m)) return `<span class="live">${m.GoalsHomeTeam ?? 0}–${m.GoalsAwayTeam ?? 0}</span>`;
  return '–';
}

function matchDayList(matches) {
  if (matches.length === 0) return '<div class="muted small">Inga matcher.</div>';
  let html = '';
  let lastDay = '';
  for (const m of matches) {
    const day = fmtDay.format(matchTime(m));
    if (day !== lastDay) {
      html += `<div class="day">${h(day)}</div>`;
      lastDay = day;
    }
    html += `<div class="match-row" data-match="${m.MatchID}">
      <span class="time">${isLive(m) ? '<span class="live">LIVE</span>' : h(fmtTime.format(matchTime(m)))}</span>
      <span class="home">${h(m.HomeTeam.trim())}</span>
      <span class="score">${scoreText(m)}</span>
      <span class="away">${h(m.AwayTeam.trim())}</span>
    </div>`;
  }
  return html;
}

function renderSeriesTable(data) {
  const now = new Date();
  const table = buildTable(data.matches, state.tableScope);
  const live = data.matches.filter(m => isLive(m, now)).sort((a, b) => matchTime(a) - matchTime(b));
  const recent = data.matches.filter(isPlayed).sort((a, b) => matchTime(b) - matchTime(a)).slice(0, 8)
    .sort((a, b) => matchTime(a) - matchTime(b));
  const upcoming = data.matches.filter(m => isUpcoming(m, now)).sort((a, b) => matchTime(a) - matchTime(b)).slice(0, 8);
  const history = state.tableScope === 'all' ? positionHistory(data.matches) : null;

  const scopeTabs = [['all', 'Totalt'], ['home', 'Hemma'], ['away', 'Borta']]
    .map(([k, label]) => `<button data-scope="${k}" class="${state.tableScope === k ? 'active' : ''}">${label}</button>`).join('');

  let html = `<div class="tabs">${scopeTabs}</div>`;
  if (table.length === 0) {
    html += '<div class="empty">Inga spelade matcher ännu.</div>';
  } else {
    html += `<div class="table-wrap"><table>
      <thead><tr><th>#</th><th class="l">Lag</th><th>M</th><th>V</th><th>O</th><th>F</th><th>Mål</th><th>+/-</th><th>P</th><th class="l">Form</th></tr></thead>
      <tbody>${table.map((t, i) => `<tr>
        <td>${i + 1}</td>
        <td class="l name"><span class="link" data-team="${h(t.teamName)}">${h(t.teamName)}</span></td>
        <td>${t.played}</td><td>${t.wins}</td><td>${t.draws}</td><td>${t.losses}</td>
        <td>${t.goalsFor}–${t.goalsAgainst}</td><td>${signed(t.goalDiff)}</td><td><b>${t.points}</b></td>
        <td class="l">${formBadges(t.form)}</td>
      </tr>`).join('')}</tbody>
    </table></div>`;
  }

  if (live.length > 0) html += `<h2><span class="live">●</span> Pågående</h2><div class="card">${matchDayList(live)}</div>`;
  html += `<h2>Senaste resultat</h2><div class="card">${matchDayList(recent)}</div>`;
  html += `<h2>Kommande</h2><div class="card">${matchDayList(upcoming)}</div>`;

  if (history) {
    const teams = [...history.ranks.keys()];
    html += `<h2>Tabellplacering omgång för omgång</h2><div class="table-wrap"><table>
      <thead><tr><th class="l">Lag</th>${history.rounds.map(r => `<th class="rank-cell">${r}</th>`).join('')}</tr></thead>
      <tbody>${teams.map(team => `<tr><td class="l name"><span class="link" data-team="${h(team)}">${h(team)}</span></td>
        ${history.ranks.get(team).map(r => `<td class="rank-cell">${r ?? ''}</td>`).join('')}</tr>`).join('')}</tbody>
    </table></div>`;
  }
  return html;
}

function coverageNote(data) {
  const have = data.played.length;
  if (have >= data.playedCount) return '';
  return `<div class="muted small" style="margin-bottom:8px">Bygger på ${have} av ${data.playedCount} spelade matcher – resten hämtas i bakgrunden.</div>`;
}

const STANDING_COLUMNS = [
  ['matches', 'M'], ['goals', 'G'], ['assists', 'A'], ['points', 'P'], ['penaltyMinutes', 'Utv'], ['ppGoals', 'PP'], ['shGoals', 'BP'], ['gwGoals', 'GWG']
];

function sortStandings(list) {
  const { key, desc } = state.standingsSort;
  const dir = desc ? -1 : 1;
  return [...list].sort((a, b) => {
    const primary = key === 'name' || key === 'team'
      ? a[key].localeCompare(b[key], 'sv') * dir
      : (a[key] - b[key]) * dir;
    return primary || b.points - a.points || b.goals - a.goals || a.name.localeCompare(b.name, 'sv');
  });
}

function standingsTable(list, { showTeam = true } = {}) {
  const sorted = sortStandings(list);
  const th = (key, label, cls = '') => {
    const active = state.standingsSort.key === key;
    return `<th data-sort="${key}" class="${cls} ${active ? 'sorted' : ''}">${label}${active ? (state.standingsSort.desc ? ' ▾' : ' ▴') : ''}</th>`;
  };
  return `<div class="table-wrap"><table>
    <thead><tr><th>#</th>${th('name', 'Namn', 'l')}${showTeam ? th('team', 'Lag', 'l') : '<th class="l">Pos</th><th>Född</th>'}${STANDING_COLUMNS.map(([k, l]) => th(k, l)).join('')}</tr></thead>
    <tbody>${sorted.map((p, i) => `<tr>
      <td>${i + 1}</td>
      <td class="l name">${h(p.name)}</td>
      ${showTeam ? `<td class="l name"><span class="link" data-team="${h(p.team)}">${h(p.team)}</span></td>` : `<td class="l">${h(p.position)}</td><td>${p.birthYear || ''}</td>`}
      ${STANDING_COLUMNS.map(([k]) => `<td>${k === 'points' ? `<b>${p[k]}</b>` : p[k]}</td>`).join('')}
    </tr>`).join('')}</tbody>
  </table></div>`;
}

function renderStandings(data) {
  const all = playerStandings(data.played);
  const teams = [...new Set(all.map(p => p.team))].sort((a, b) => a.localeCompare(b, 'sv'));
  const list = state.standingsTeam ? all.filter(p => p.team === state.standingsTeam) : all;
  if (all.length === 0) return `${coverageNote(data)}<div class="empty">Ingen matchdata insamlad ännu.</div>`;
  return `${coverageNote(data)}
    <label class="small">Lag: <select data-filter="team">
      <option value="">Alla lag</option>
      ${teams.map(t => `<option ${t === state.standingsTeam ? 'selected' : ''}>${h(t)}</option>`).join('')}
    </select></label>
    <div style="margin-top:8px">${standingsTable(list.slice(0, state.standingsTeam ? undefined : 150))}</div>`;
}

function goalieTable(list) {
  const sorted = [...list].sort((a, b) => (b.savePercentage ?? -1) - (a.savePercentage ?? -1) || a.gaa - b.gaa);
  return `<div class="table-wrap"><table>
    <thead><tr><th class="l">Namn</th><th class="l">Lag</th><th>M</th><th>Min</th><th>IM</th><th>IM/60</th><th>Rädd %</th></tr></thead>
    <tbody>${sorted.map(g => `<tr>
      <td class="l name">${h(g.name)}</td>
      <td class="l name"><span class="link" data-team="${h(g.team)}">${h(g.team)}</span></td>
      <td>${g.matches}</td><td>${Math.round(g.seconds / 60)}</td><td>${g.goalsAgainst}</td>
      <td>${g.gaa.toFixed(2)}</td><td>${pct(g.savePercentage)}</td>
    </tr>`).join('')}</tbody>
  </table></div>`;
}

function renderGoalies(data) {
  const goalies = goalieStandings(data.played).filter(g => g.seconds >= 60 * 60 || state.standingsTeam);
  if (goalies.length === 0) return `${coverageNote(data)}<div class="empty">Ingen målvaktsdata insamlad ännu.</div>`;
  return `${coverageNote(data)}<div class="muted small" style="margin-bottom:6px">Minst 60 spelade minuter.</div>${goalieTable(goalies)}`;
}

async function renderSeries(route) {
  const data = await loadSeries(route);
  if (!data) {
    titleEl.textContent = 'Serie';
    return route.seasonId
      ? '<div class="empty">Hämtar seriens matchlista…</div>'
      : '<div class="empty">Öppna seriens sida på stats.innebandy.se för att läsa in den.</div>';
  }
  titleEl.textContent = data.name || 'Serie';

  const tabs = [['table', 'Tabell'], ['standings', 'Poängliga'], ['goalies', 'Målvakter']]
    .map(([k, label]) => `<button data-series-tab="${k}" class="${state.seriesTab === k ? 'active' : ''}">${label}</button>`).join('');

  const body = state.seriesTab === 'standings' ? renderStandings(data)
    : state.seriesTab === 'goalies' ? renderGoalies(data)
      : renderSeriesTable(data);
  return `<div class="tabs">${tabs}</div>${body}`;
}

async function renderTeam(route) {
  const data = await loadSeries(route);
  titleEl.textContent = route.team;
  if (!data) return '<div class="empty">Seriens data är inte inläst ännu.</div>';

  const table = buildTable(data.matches);
  const rank = table.findIndex(t => t.teamName === route.team);
  const entry = table[rank];
  const teamMatches = data.matches.filter(m => m.HomeTeam.trim() === route.team || m.AwayTeam.trim() === route.team);
  const now = new Date();
  const played = teamMatches.filter(isPlayed).sort((a, b) => matchTime(a) - matchTime(b));
  const upcoming = teamMatches.filter(m => !isPlayed(m)).sort((a, b) => matchTime(a) - matchTime(b));
  const players = playerStandings(data.played).filter(p => p.team === route.team);
  const goalies = goalieStandings(data.played).filter(g => g.team === route.team);

  let html = '';
  if (entry) {
    html += `<div class="card">
      <div style="display:flex;justify-content:space-between;align-items:center">
        <div><b>${rank + 1}:a</b> i ${h(data.name)}</div>${formBadges(entry.form)}
      </div>
      <div class="muted small" style="margin-top:4px">${entry.played} matcher · ${entry.wins}V ${entry.draws}O ${entry.losses}F · ${entry.goalsFor}–${entry.goalsAgainst} (${signed(entry.goalDiff)}) · <b>${entry.points} p</b></div>
    </div>`;
  }

  const live = upcoming.filter(m => isLive(m, now));
  if (live.length) html += `<h2><span class="live">●</span> Pågående</h2><div class="card">${matchDayList(live)}</div>`;
  html += `<h2>Kommande</h2><div class="card">${matchDayList(upcoming.filter(m => !isLive(m, now)).slice(0, 6))}</div>`;
  html += `<h2>Spelade matcher</h2><div class="card">${matchDayList([...played].reverse())}</div>`;

  html += `<h2>Spelare</h2>${coverageNote(data)}`;
  html += players.length ? standingsTable(players, { showTeam: false }) : '<div class="muted small">Ingen spelardata insamlad ännu.</div>';
  if (goalies.length) html += `<h2>Målvakter</h2>${goalieTable(goalies)}`;
  return html;
}

function eventLine(e, match) {
  const team = e.isHome ? match.HomeTeam.trim() : match.AwayTeam.trim();
  let what;
  if (isGoalEvent(e)) {
    const badges = [
      e.strength === 'pp' ? '<span class="badge pp">PP</span>' : '',
      e.strength === 'sh' ? '<span class="badge sh">BP</span>' : '',
      e.kind === 'penaltyShotGoal' ? '<span class="badge">Straff</span>' : '',
      e.kind === 'emptyNetGoal' ? '<span class="badge">Tom kasse</span>' : '',
      e.isGameWinner ? '<span class="badge gw">GWG</span>' : ''
    ].join('');
    what = `<b>${e.scoreHome}–${e.scoreAway}</b> ${h(e.playerName)}${e.assistName ? ` <span class="muted">(${h(e.assistName)})</span>` : ''}${badges}`;
  } else if (e.kind === 'penalty') {
    what = `<span class="muted">Utvisning</span> ${h(e.playerName)} <span class="muted">${h(e.description)}</span>`;
  } else if (e.kind === 'timeout') {
    what = '<span class="muted">Timeout</span>';
  } else {
    what = `<span class="muted">${h(e.description)}</span> ${h(e.playerName)}`;
  }
  return `<div class="ev ${e.isHome ? 'home' : 'away'}">
    <span class="muted">${clock(e.minute, e.second)}</span>
    <span class="what">${what}<div class="muted small">${h(team)}</div></span>
  </div>`;
}

function lineupTable(players, lines) {
  const byId = new Map(lines.map(l => [l.playerId, l]));
  const sorted = [...players].sort((a, b) => (a.ShirtNo ?? 999) - (b.ShirtNo ?? 999));
  return `<div class="table-wrap"><table>
    <thead><tr><th>#</th><th class="l">Namn</th><th class="l">Pos</th><th>G</th><th>A</th><th>P</th><th>Utv</th></tr></thead>
    <tbody>${sorted.map(p => {
      const l = byId.get(p.PlayerID) ?? { goals: 0, assists: 0, penaltyMinutes: 0 };
      return `<tr><td>${p.ShirtNo ?? ''}</td><td class="l name">${h((p.Name ?? '').trim())}${p.Captain ? ' <span class="badge">C</span>' : ''}</td>
        <td class="l">${h(p.Position ?? '')}</td><td>${l.goals}</td><td>${l.assists}</td><td><b>${l.goals + l.assists}</b></td><td>${l.penaltyMinutes}</td></tr>`;
    }).join('')}</tbody>
  </table></div>`;
}

async function renderMatch(route) {
  const [detail] = (await getMatchDetails([route.matchId])).values();
  const competitionId = route.competitionId ?? detail?.match.CompetitionID;
  const comp = competitionId ? await getCompetition(competitionId) : null;
  const listMatch = comp?.matches?.find(m => m.MatchID === route.matchId);
  const match = detail?.match ?? listMatch;

  // Matchen saknas eller sparades innan den var färdigspelad – hämta matchsidan
  const needsFetch = !detail || (listMatch && isPlayed(listMatch) && !isPlayed(detail.match)) || (detail && isLive(detail.match));
  if (needsFetch && route.seasonId && competitionId) {
    collect([{ ...matchJob(route.seasonId, competitionId, match ?? { MatchID: route.matchId, HomeTeam: 'Match', AwayTeam: `${route.matchId}` }), priority: true }]);
  }

  if (!match) {
    titleEl.textContent = 'Match';
    return '<div class="empty">Matchen är inte inläst ännu. Öppna matchsidan på stats.innebandy.se.</div>';
  }

  const home = match.HomeTeam.trim();
  const away = match.AwayTeam.trim();
  titleEl.textContent = `${home} – ${away}`;
  const teamLink = name => competitionId ? `<span class="link" data-team="${h(name)}">${h(name)}</span>` : h(name);

  let html = `<div class="scoreboard">
    <div class="teams">
      <div class="team">${teamLink(home)}</div>
      <div class="big">${isPlayed(match) || isLive(match) ? scoreText(match) : '–'}</div>
      <div class="team">${teamLink(away)}</div>
    </div>
    <div class="muted small">${h(fmtDate.format(matchTime(match)))}${isLive(match) ? ' · <span class="live">Pågår</span>' : ''}</div>
  </div>`;

  const a = detail?.analysis;
  if (a) {
    const periodGoals = a.periodLengths.map((_, i) => {
      const goals = a.events.filter(e => isGoalEvent(e) && e.period === i + 1);
      return `${goals.filter(g => g.isHome).length}–${goals.filter(g => !g.isHome).length}`;
    });
    html += `<div class="card"><dl class="facts">
      ${periodGoals.length ? `<dt>Perioder</dt><dd>${periodGoals.join(', ')}</dd>` : ''}
      ${a.shots.length ? `<dt>Skott</dt><dd>${a.shots.reduce((s, x) => s + x.home, 0)}–${a.shots.reduce((s, x) => s + x.away, 0)} <span class="muted">(${a.shots.map(s => `${s.home}–${s.away}`).join(', ')})</span></dd>` : ''}
      <dt>Powerplay</dt><dd>${a.events.filter(e => isGoalEvent(e) && e.isHome && e.strength === 'pp').length}/${a.homePpOpportunities} – ${a.events.filter(e => isGoalEvent(e) && !e.isHome && e.strength === 'pp').length}/${a.awayPpOpportunities}</dd>
      <dt>Utvisningar</dt><dd>${a.events.filter(e => e.kind === 'penalty' && e.isHome).reduce((s, e) => s + e.penaltyMinutes, 0)}–${a.events.filter(e => e.kind === 'penalty' && !e.isHome).reduce((s, e) => s + e.penaltyMinutes, 0)} min</dd>
      ${match.Venue ? `<dt>Arena</dt><dd>${h(match.Venue)}</dd>` : ''}
      ${match.Spectators ? `<dt>Publik</dt><dd>${match.Spectators}</dd>` : ''}
      ${match.Referee1 ? `<dt>Domare</dt><dd>${h([match.Referee1, match.Referee2].filter(Boolean).join(', '))}</dd>` : ''}
      ${match.RoundName || match.Round ? `<dt>Omgång</dt><dd>${h(match.RoundName || match.Round)}</dd>` : ''}
    </dl></div>`;

    if (a.events.length) {
      html += '<h2>Händelseförlopp</h2><div class="timeline">';
      let period = null;
      for (const e of a.events) {
        if (e.period !== period) {
          period = e.period;
          html += `<div class="period">${h(e.periodName || `Period ${e.period}`)}</div>`;
        }
        html += eventLine(e, match);
      }
      html += '</div>';
    }

    if (a.goalies.length) {
      html += `<h2>Målvakter</h2><div class="table-wrap"><table>
        <thead><tr><th class="l">Namn</th><th class="l">Lag</th><th>Tid</th><th>IM</th><th>Skott</th><th>Rädd %</th></tr></thead>
        <tbody>${a.goalies.map(g => `<tr><td class="l name">${h(g.name)}</td><td class="l name">${h(g.isHome ? home : away)}</td>
          <td>${minutes(g.seconds)}</td><td>${g.goalsAgainst}</td><td>${g.hasShots ? Math.round(g.shotsAgainst) : '–'}</td><td>${pct(g.savePercentage)}</td></tr>`).join('')}</tbody>
      </table></div>`;
    }

    const lineup = detail.lineup;
    if (lineup && ((lineup.HomeTeamPlayers?.length ?? 0) + (lineup.AwayTeamPlayers?.length ?? 0)) > 0) {
      const lines = buildPlayerLines(detail);
      html += `<h2>${h(home)}</h2>${lineupTable(lineup.HomeTeamPlayers ?? [], lines)}`;
      html += `<h2>${h(away)}</h2>${lineupTable(lineup.AwayTeamPlayers ?? [], lines)}`;
    }
  } else if (needsFetch) {
    html += '<div class="empty">Hämtar matchdetaljer…</div>';
  }

  if (comp) {
    const meetings = comp.matches
      .filter(m => m.MatchID !== match.MatchID && isPlayed(m)
        && [m.HomeTeam.trim(), m.AwayTeam.trim()].sort().join('|') === [home, away].sort().join('|'))
      .sort((x, y) => matchTime(x) - matchTime(y));
    if (meetings.length) html += `<h2>Tidigare möten i serien</h2><div class="card">${matchDayList(meetings)}</div>`;
  }

  return html;
}

let renderToken = 0;

async function render() {
  const token = ++renderToken;
  const route = state.route;
  backButton.hidden = state.stack.length === 0;

  let html;
  if (!route) {
    titleEl.textContent = 'Innebandystats';
    html = '<div class="empty">Öppna stats.innebandy.se i den här fliken för att se statistik.</div>';
  } else if (route.view === 'none') {
    titleEl.textContent = 'Innebandystats';
    html = '<div class="empty">Gå in på en serie eller en match på stats.innebandy.se.</div>';
  } else if (route.view === 'series') {
    html = await renderSeries(route);
  } else if (route.view === 'team') {
    html = await renderTeam(route);
  } else if (route.view === 'match') {
    html = await renderMatch(route);
  }

  if (token !== renderToken) return;
  app.innerHTML = html;
  renderCollector(await getCollectorStatus());
}

let renderTimer = null;
function scheduleRender(delay = 300) {
  clearTimeout(renderTimer);
  renderTimer = setTimeout(render, delay);
}

// ---- Navigering ----

function navigate(route) {
  if (state.route) state.stack.push(state.route);
  state.route = route;
  window.scrollTo(0, 0);
  render();
}

function seriesContext() {
  const r = state.route ?? {};
  return { competitionId: r.competitionId, seasonId: r.seasonId };
}

app.addEventListener('click', event => {
  const target = event.target.closest('[data-team],[data-match],[data-series-tab],[data-scope],[data-sort]');
  if (!target) return;

  if (target.dataset.team) {
    const ctx = seriesContext();
    if (ctx.competitionId) navigate({ view: 'team', ...ctx, team: target.dataset.team });
  } else if (target.dataset.match) {
    navigate({ view: 'match', ...seriesContext(), matchId: +target.dataset.match });
  } else if (target.dataset.seriesTab) {
    state.seriesTab = target.dataset.seriesTab;
    render();
  } else if (target.dataset.scope) {
    state.tableScope = target.dataset.scope;
    render();
  } else if (target.dataset.sort) {
    const key = target.dataset.sort;
    state.standingsSort = state.standingsSort.key === key
      ? { key, desc: !state.standingsSort.desc }
      : { key, desc: key !== 'name' && key !== 'team' };
    render();
  }
});

app.addEventListener('change', event => {
  if (event.target.dataset.filter === 'team') {
    state.standingsTeam = event.target.value;
    render();
  }
});

collectorEl.addEventListener('click', event => {
  if (event.target.dataset.action === 'retry') {
    requested.clear();
    collect([], { retry: true }).then(() => render());
  }
});

backButton.addEventListener('click', () => {
  state.route = state.stack.pop() ?? null;
  render();
});

// Följ den aktiva fliken: ny sida på stats.innebandy.se ger ny utgångsvy
async function syncWithActiveTab() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab) return;
  if (tab.id === state.tabId && tab.url === state.tabUrl) return;

  const context = parseContext(tab.url ?? '');
  // En omladdad sida har tappat sin insamlingskö – tillåt att jobben skickas igen
  requested.clear();
  state.tabId = context ? tab.id : null;
  state.tabUrl = tab.url;
  state.contentMissing = false;
  state.stack = [];
  state.route = rootRouteFor(context);
  render();
}

chrome.tabs.onActivated.addListener(syncWithActiveTab);
chrome.tabs.onUpdated.addListener((_tabId, changeInfo) => {
  if (changeInfo.url) syncWithActiveTab();
});
chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== 'local') return;
  const keys = Object.keys(changes);
  if (keys.some(k => k === 'sync:status' || k === 'sync:settings')) renderSync();
  if (keys.includes('collector:status')) getCollectorStatus().then(renderCollector);
  // Bara insamlad data påverkar vyerna – inte statusnycklar eller synkens utkorg
  if (keys.some(k => !k.startsWith('sync:') && k !== 'collector:status')) scheduleRender();
});

// ---- Synkstatus ----

const syncEl = document.getElementById('sync');
const fmtClock = new Intl.DateTimeFormat('sv-SE', { hour: '2-digit', minute: '2-digit' });

function sinceText(timestamp) {
  if (!timestamp) return '';
  const d = new Date(timestamp);
  return d.toDateString() === new Date().toDateString() ? fmtClock.format(d) : d.toLocaleString('sv-SE', { dateStyle: 'short', timeStyle: 'short' });
}

async function renderSync() {
  const stored = await chrome.storage.local.get(['sync:settings', 'sync:status']);
  const settings = stored['sync:settings'];
  const status = { pending: 0, sentTotal: 0, ...(stored['sync:status'] ?? {}) };
  if (!settings?.enabled) {
    syncEl.hidden = true;
    return;
  }

  syncEl.hidden = false;
  if (status.lastError) {
    syncEl.className = 'error';
    syncEl.innerHTML = `Synk misslyckades ${h(sinceText(status.lastErrorAt))}: ${h(status.lastError)} · ${status.pending} väntar <span class="link" data-action="sync-retry">Försök igen</span>`;
    return;
  }
  syncEl.className = '';
  const pending = status.pending > 0 ? `${status.pending} väntar` : '<span class="ok">allt skickat</span>';
  const last = status.lastSuccessAt ? ` · senast ${h(sinceText(status.lastSuccessAt))}` : '';
  syncEl.innerHTML = `Synk: ${pending} · ${status.sentTotal} skickade${last}`;
}

syncEl.addEventListener('click', event => {
  if (event.target.dataset.action === 'sync-retry') chrome.runtime.sendMessage({ type: 'ibs-sync-flush' }).catch(() => {});
});

renderSync();
syncWithActiveTab();
