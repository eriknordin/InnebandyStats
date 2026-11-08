// Statistiklogik porterad från C#-appen (MatchState, MatchAnalyzer, InnebandyApiService).
// Datan har samma form som API:t: PascalCase-fält som MatchID, HomeTeam, Events osv.

// ---- Matchläge ----

export const PLAYED_STATUS = 4;
const LIVE_WINDOW_MS = 3 * 60 * 60 * 1000;

// Matchtider är svensk lokal tid utan tidszon; tolkas som webbläsarens lokala tid
export const matchTime = m => new Date(m.MatchDateTime);

export const isPlayed = m =>
  m.MatchStatus === PLAYED_STATUS && m.GoalsHomeTeam != null && m.GoalsAwayTeam != null;

export const isLive = (m, now = new Date()) => {
  const start = matchTime(m);
  return !isPlayed(m) && start <= now && now - start < LIVE_WINDOW_MS;
};

export const isUpcoming = (m, now = new Date()) => !isPlayed(m) && matchTime(m) > now;

// ---- Tabell ----

// scope: all, home (bara hemmamatcher) eller away (bara bortamatcher)
export function buildTable(matches, scope = 'all') {
  const teams = new Map();

  const ensure = (id, name) => {
    if (!teams.has(id)) {
      teams.set(id, {
        teamId: id, teamName: (name ?? '').trim(),
        played: 0, wins: 0, draws: 0, losses: 0, goalsFor: 0, goalsAgainst: 0, form: []
      });
    }
    return teams.get(id);
  };

  const add = (team, gf, ga) => {
    team.played++;
    team.goalsFor += gf;
    team.goalsAgainst += ga;
    if (gf > ga) { team.wins++; team.form.push('V'); }
    else if (gf < ga) { team.losses++; team.form.push('F'); }
    else { team.draws++; team.form.push('O'); }
  };

  const played = matches.filter(isPlayed).sort((a, b) => matchTime(a) - matchTime(b));
  for (const m of played) {
    const home = ensure(m.HomeTeamID, m.HomeTeam);
    const away = ensure(m.AwayTeamID, m.AwayTeam);
    if (scope !== 'away') add(home, m.GoalsHomeTeam, m.GoalsAwayTeam);
    if (scope !== 'home') add(away, m.GoalsAwayTeam, m.GoalsHomeTeam);
  }

  return [...teams.values()]
    .map(t => ({ ...t, form: t.form.slice(-5), goalDiff: t.goalsFor - t.goalsAgainst, points: t.wins * 3 + t.draws }))
    .sort((a, b) => b.points - a.points
      || b.goalDiff - a.goalDiff
      || b.goalsFor - a.goalsFor
      || a.teamName.localeCompare(b.teamName, 'sv'));
}

// Tabellplacering efter varje omgång
export function positionHistory(matches) {
  const played = matches.filter(m => isPlayed(m) && m.Round > 0);
  const rounds = [...new Set(played.map(m => m.Round))].sort((a, b) => a - b);
  if (rounds.length < 2) return null;

  const finalTable = buildTable(played);
  const ranks = new Map(finalTable.map(t => [t.teamName, []]));
  for (const round of rounds) {
    const table = buildTable(played.filter(m => m.Round <= round));
    for (const team of finalTable) {
      const index = table.findIndex(t => t.teamName === team.teamName);
      ranks.get(team.teamName).push(index >= 0 ? index + 1 : null);
    }
  }
  return { rounds, ranks };
}

// ---- Matchanalys ----

const TYPE = {
  goal: 1, penalty: 2, timeoutHome: 3, timeoutAway: 4, penaltyShotGoal: 5,
  missedPenaltyShot: 6, periodEnd: 9, goalieIn: 10, goalieOut: 11, emptyNetGoal: 12
};
const REGULAR_PERIOD_SECONDS = 20 * 60;
const SKATERS_ON_COURT = 5;
const MAX_PENALIZED = 2;

// Utvisningsminuter och hur länge laget spelar med en man mindre (10 min påverkar inte spelstyrkan)
export function parsePenalty(name = '') {
  if (/matchstraff/i.test(name)) return { minutes: 5, strengthSeconds: 5 * 60 };
  const match = /(\d+(?:\s*\+\s*\d+)*)\s*min/i.exec(name);
  if (!match) return { minutes: 2, strengthSeconds: 2 * 60 };
  const parts = match[1].split('+').map(p => parseInt(p.trim(), 10) || 0);
  return {
    minutes: parts.reduce((s, p) => s + p, 0),
    strengthSeconds: parts.filter(p => p !== 10).reduce((s, p) => s + p, 0) * 60
  };
}

export const isGoalEvent = e => e.kind === 'goal' || e.kind === 'penaltyShotGoal' || e.kind === 'emptyNetGoal';

export function analyzeMatch(match) {
  const analysis = {
    events: [], goalies: [], shots: [], periodLengths: [], totalSeconds: 0,
    homePpOpportunities: 0, awayPpOpportunities: 0
  };
  const events = [...(match.Events ?? [])].sort((a, b) =>
    a.Period - b.Period || a.Minute - b.Minute || a.Second - b.Second || a.MatchEventID - b.MatchEventID);

  // Periodlängder: periodslut om det finns, annars senaste händelse (minst 20 min för ordinarie perioder)
  const maxPeriod = events.length > 0 ? Math.max(1, ...events.map(e => e.Period)) : 0;
  const offsets = [];
  let total = 0;
  for (let p = 1; p <= maxPeriod; p++) {
    const inPeriod = events.filter(e => e.Period === p);
    const periodEnd = Math.max(0, ...inPeriod.filter(e => e.MatchEventTypeID === TYPE.periodEnd).map(e => e.Minute * 60 + e.Second));
    const lastEvent = Math.max(0, ...inPeriod.map(e => e.Minute * 60 + e.Second));
    const length = periodEnd > 0 ? periodEnd : Math.max(lastEvent, p <= 3 ? REGULAR_PERIOD_SECONDS : 0);
    offsets.push(total);
    analysis.periodLengths.push(length);
    total += length;
  }
  analysis.totalSeconds = total;

  const timeOf = e => (e.Period >= 1 && e.Period <= offsets.length ? offsets[e.Period - 1] : 0) + e.Minute * 60 + e.Second;
  const isHome = e => e.IsHomeTeam ?? (e.MatchTeamID === match.HomeMatchTeamID);

  const penalties = [];
  const penalizedCount = (home, t) =>
    Math.min(MAX_PENALIZED, penalties.filter(p => p.isHome === home && p.start <= t && t < p.end).length);

  const currentGoalie = new Map();
  const stints = [];
  const goalsAgainst = new Map();
  const goalieKey = (home, id) => `${home}|${id}`;

  const closeGoalie = (home, t) => {
    const g = currentGoalie.get(home);
    if (!g) return;
    currentGoalie.delete(home);
    stints.push({ isHome: home, playerId: g.playerId, name: g.name, start: g.start, end: t });
  };

  for (const e of events) {
    const t = timeOf(e);

    if (e.MatchEventTypeID === TYPE.goalieIn && e.PlayerID > 0) {
      const home = isHome(e);
      closeGoalie(home, t);
      currentGoalie.set(home, { playerId: e.PlayerID, name: e.PlayerName ?? '', start: t });
      continue;
    }
    if (e.MatchEventTypeID === TYPE.goalieOut) {
      const home = isHome(e);
      const g = currentGoalie.get(home);
      if (g && (e.PlayerID === 0 || g.playerId === e.PlayerID)) closeGoalie(home, t);
      continue;
    }

    const item = {
      kind: null, period: e.Period, periodName: e.PeriodName, minute: e.Minute, second: e.Second,
      timeSeconds: t, isHome: false, playerId: e.PlayerID, playerName: e.PlayerName ?? '',
      playerShirtNo: e.PlayerShirtNo, assistId: e.PlayerAssistID, assistName: e.PlayerAssistName ?? '',
      scoreHome: e.GoalsHomeTeam, scoreAway: e.GoalsAwayTeam, description: '', penaltyMinutes: 0,
      strength: 'even', isGameWinner: false
    };

    switch (e.MatchEventTypeID) {
      case TYPE.goal:
      case TYPE.penaltyShotGoal:
      case TYPE.emptyNetGoal: {
        item.kind = e.MatchEventTypeID === TYPE.penaltyShotGoal ? 'penaltyShotGoal'
          : e.MatchEventTypeID === TYPE.emptyNetGoal ? 'emptyNetGoal' : 'goal';
        item.isHome = isHome(e);
        const own = SKATERS_ON_COURT - penalizedCount(item.isHome, t);
        const opponent = SKATERS_ON_COURT - penalizedCount(!item.isHome, t);
        item.strength = own > opponent ? 'pp' : own < opponent ? 'sh' : 'even';
        if (item.strength === 'pp') endMinorPenalty(penalties, !item.isHome, t);

        const goalie = currentGoalie.get(!item.isHome);
        if (item.kind !== 'emptyNetGoal' && goalie) {
          const key = goalieKey(!item.isHome, goalie.playerId);
          goalsAgainst.set(key, (goalsAgainst.get(key) ?? 0) + 1);
        }
        break;
      }
      case TYPE.penalty: {
        item.kind = 'penalty';
        item.isHome = isHome(e);
        item.description = e.PenaltyName ?? '';
        const { minutes, strengthSeconds } = parsePenalty(e.PenaltyName ?? '');
        item.penaltyMinutes = minutes;
        if (strengthSeconds > 0) {
          penalties.push({ isHome: item.isHome, start: t, end: t + strengthSeconds, strengthSeconds });
          if (item.isHome) analysis.awayPpOpportunities++;
          else analysis.homePpOpportunities++;
        }
        break;
      }
      case TYPE.missedPenaltyShot:
        item.kind = 'missedPenaltyShot';
        item.isHome = isHome(e);
        item.description = e.MatchEventType;
        break;
      case TYPE.timeoutHome:
      case TYPE.timeoutAway:
        item.kind = 'timeout';
        item.isHome = e.MatchEventTypeID === TYPE.timeoutHome;
        item.description = 'Timeout';
        break;
      default:
        continue;
    }

    analysis.events.push(item);
  }

  for (const home of [...currentGoalie.keys()]) closeGoalie(home, total);

  markGameWinner(analysis, match);
  addShots(analysis, match);
  addGoalies(analysis, stints, goalsAgainst, offsets, goalieKey);
  return analysis;
}

// Ett powerplaymål avslutar motståndarens tidigast utgående 2-minutersutvisning
function endMinorPenalty(penalties, penalizedIsHome, t) {
  const penalty = penalties
    .filter(p => p.isHome === penalizedIsHome && p.start <= t && t < p.end && (p.strengthSeconds === 120 || p.strengthSeconds === 240))
    .sort((a, b) => a.end - b.end)[0];
  if (!penalty) return;
  // 2+2: mål under första delen startar andra delen direkt
  penalty.end = penalty.strengthSeconds === 240 && penalty.end - t > 120 ? t + 120 : t;
}

function markGameWinner(analysis, match) {
  const goals = analysis.events.filter(isGoalEvent);
  const home = match.GoalsHomeTeam ?? goals.filter(g => g.isHome).length;
  const away = match.GoalsAwayTeam ?? goals.filter(g => !g.isHome).length;
  if (home === away) return;
  const winnerIsHome = home > away;
  const loserGoals = Math.min(home, away);
  const winner = goals.find(g => g.isHome === winnerIsHome && (winnerIsHome ? g.scoreHome : g.scoreAway) === loserGoals + 1);
  if (winner) winner.isGameWinner = true;
}

function addShots(analysis, match) {
  if (!match.ShotsOnGoal) return;
  analysis.shots = match.ShotsOnGoal
    .filter(s => s.ShotsHomeTeam != null || s.ShotsAwayTeam != null)
    .sort((a, b) => a.Period - b.Period)
    .map(s => ({ period: s.Period, home: s.ShotsHomeTeam ?? 0, away: s.ShotsAwayTeam ?? 0 }));
  // Inga registrerade skott alls räknas som saknad data
  if (analysis.shots.every(s => s.home === 0 && s.away === 0)) analysis.shots = [];
}

function addGoalies(analysis, stints, goalsAgainst, offsets, goalieKey) {
  const groups = new Map();
  for (const s of stints) {
    const key = goalieKey(s.isHome, s.playerId);
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(s);
  }

  const hasShots = analysis.shots.length > 0;
  for (const [key, group] of groups) {
    const stats = {
      playerId: group[0].playerId, isHome: group[0].isHome, name: group[0].name,
      seconds: group.reduce((sum, s) => sum + s.end - s.start, 0),
      goalsAgainst: goalsAgainst.get(key) ?? 0, shotsAgainst: 0, hasShots
    };

    // Skott mot målvakten: motståndarens skott i perioden, fördelat efter speltid
    for (const shots of analysis.shots) {
      const index = shots.period - 1;
      if (index < 0 || index >= offsets.length) continue;
      const start = offsets[index];
      const length = analysis.periodLengths[index];
      if (length <= 0) continue;
      const played = group.reduce((sum, s) => sum + Math.max(0, Math.min(s.end, start + length) - Math.max(s.start, start)), 0);
      stats.shotsAgainst += (stats.isHome ? shots.away : shots.home) * played / length;
    }

    stats.savePercentage = stats.hasShots && stats.shotsAgainst > 0
      ? Math.max(0, (stats.shotsAgainst - stats.goalsAgainst) / stats.shotsAgainst * 100)
      : null;
    if (stats.seconds > 0) analysis.goalies.push(stats);
  }

  analysis.goalies.sort((a, b) => (b.isHome - a.isHome) || b.seconds - a.seconds);
}

// ---- Spelarstatistik ----

// Varje spelares insats i en match, från lineup och händelser. pm = { match, lineup, analysis }
export function buildPlayerLines(pm) {
  const { match, lineup, analysis } = pm;
  const goals = analysis.events.filter(isGoalEvent);
  const homeGoals = match.GoalsHomeTeam ?? goals.filter(g => g.isHome).length;
  const awayGoals = match.GoalsAwayTeam ?? goals.filter(g => !g.isHome).length;
  const hasLineup = Boolean(lineup && ((lineup.HomeTeamPlayers?.length ?? 0) + (lineup.AwayTeamPlayers?.length ?? 0) > 0));
  const homeTeam = match.HomeTeam.trim();
  const awayTeam = match.AwayTeam.trim();
  const lines = new Map();

  const ensure = (playerId, name, isHome) => {
    if (!lines.has(playerId)) {
      lines.set(playerId, {
        matchId: match.MatchID, matchDateTime: match.MatchDateTime, playerId, name: (name ?? '').trim(),
        shirtNo: null, position: '', captain: false, age: 0, birthYear: 0,
        isHome, team: isHome ? homeTeam : awayTeam, opponent: isHome ? awayTeam : homeTeam,
        goalsFor: isHome ? homeGoals : awayGoals, goalsAgainst: isHome ? awayGoals : homeGoals,
        played: !hasLineup, goals: 0, assists: 0, penaltyMinutes: 0, ppGoals: 0, shGoals: 0, gwGoals: 0
      });
    }
    return lines.get(playerId);
  };

  if (lineup) {
    for (const [players, isHome] of [[lineup.HomeTeamPlayers ?? [], true], [lineup.AwayTeamPlayers ?? [], false]]) {
      for (const p of players) {
        if (!(p.PlayerID > 0)) continue;
        const line = ensure(p.PlayerID, p.Name, isHome);
        Object.assign(line, {
          played: true, shirtNo: p.ShirtNo, position: p.Position ?? '', captain: Boolean(p.Captain),
          age: p.Age ?? 0, birthYear: p.BirthYear ?? 0
        });
      }
    }
  }

  for (const e of analysis.events) {
    if (isGoalEvent(e) && e.playerId > 0) {
      const scorer = ensure(e.playerId, e.playerName, e.isHome);
      scorer.goals++;
      if (e.strength === 'pp') scorer.ppGoals++;
      if (e.strength === 'sh') scorer.shGoals++;
      if (e.isGameWinner) scorer.gwGoals++;
      if (e.assistId > 0) ensure(e.assistId, e.assistName, e.isHome).assists++;
    } else if (e.kind === 'penalty' && e.playerId > 0) {
      ensure(e.playerId, e.playerName, e.isHome).penaltyMinutes += e.penaltyMinutes;
    }
  }

  return [...lines.values()];
}

const mostCommon = values => {
  const counts = new Map();
  for (const v of values) if (v) counts.set(v, (counts.get(v) ?? 0) + 1);
  return [...counts.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] ?? '';
};

// Poängliga: nycklad på (spelare, lag) för att skilja spelare som spelat i flera lag
export function playerStandings(playedMatches) {
  const groups = new Map();
  const ordered = [...playedMatches].sort((a, b) => matchTime(a.match) - matchTime(b.match));
  for (const pm of ordered) {
    for (const line of buildPlayerLines(pm)) {
      const key = `${line.playerId}|${line.team}`;
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(line);
    }
  }

  return [...groups.values()].map(lines => {
    const last = lines[lines.length - 1];
    const sum = f => lines.reduce((s, l) => s + l[f], 0);
    const withAge = [...lines].reverse().find(l => l.birthYear > 0);
    const goals = sum('goals');
    const assists = sum('assists');
    return {
      playerId: last.playerId, team: last.team, name: last.name,
      position: mostCommon(lines.map(l => l.position)),
      age: withAge?.age ?? 0, birthYear: withAge?.birthYear ?? 0,
      matches: lines.filter(l => l.played).length,
      goals, assists, points: goals + assists,
      penaltyMinutes: sum('penaltyMinutes'), ppGoals: sum('ppGoals'), shGoals: sum('shGoals'), gwGoals: sum('gwGoals')
    };
  });
}

export function goalieStandings(playedMatches) {
  const goalies = new Map();
  for (const pm of playedMatches) {
    for (const g of pm.analysis.goalies) {
      const team = (g.isHome ? pm.match.HomeTeam : pm.match.AwayTeam).trim();
      const key = `${g.playerId}|${team}`;
      if (!goalies.has(key)) {
        goalies.set(key, { playerId: g.playerId, name: g.name, team, matches: 0, seconds: 0, goalsAgainst: 0, shotsAgainst: 0, goalsAgainstWithShots: 0 });
      }
      const s = goalies.get(key);
      s.matches++;
      s.seconds += g.seconds;
      s.goalsAgainst += g.goalsAgainst;
      // Bara matcher där skott registrerats räknas in i räddningsprocenten
      if (g.hasShots) {
        s.shotsAgainst += g.shotsAgainst;
        s.goalsAgainstWithShots += g.goalsAgainst;
      }
    }
  }
  return [...goalies.values()].map(s => ({
    ...s,
    gaa: s.seconds > 0 ? s.goalsAgainst * 3600 / s.seconds : 0,
    savePercentage: s.shotsAgainst > 0 ? Math.max(0, (s.shotsAgainst - s.goalsAgainstWithShots) / s.shotsAgainst * 100) : null
  }));
}

// Det publika API:t skickar bara MatchTeamID på händelser – härled lag från matchen
export function fillEventTeams(match) {
  for (const e of match.Events ?? []) {
    if (!e.MatchTeamID) continue;
    if (e.MatchTeamID === match.HomeMatchTeamID) {
      e.IsHomeTeam ??= true;
      e.MatchTeamName ||= match.HomeTeam;
    } else if (e.MatchTeamID === match.AwayMatchTeamID) {
      e.IsHomeTeam ??= false;
      e.MatchTeamName ||= match.AwayTeam;
    }
  }
  return match;
}
