// Läser det content.js har sparat i chrome.storage.local
import { analyzeMatch, fillEventTeams, isPlayed } from './stats.js';

// Sidor på stats.innebandy.se som hämtar viss data
export const sitePaths = {
  matchList: (seasonId, competitionId) => `/sasong/${seasonId}/serie/${competitionId}/spelprogram`,
  match: (seasonId, competitionId, matchId) => `/sasong/${seasonId}/serie/${competitionId}/match/${matchId}`
};

// Matchlistan anses färsk så här länge innan den laddas om i bakgrunden
export const MATCH_LIST_MAX_AGE_MS = 10 * 60 * 1000;

const analysisCache = new Map();

export async function getCompetition(competitionId) {
  const key = `comp:${competitionId}`;
  const result = await chrome.storage.local.get(key);
  return result[key] ?? null;
}

export async function getCollectorStatus() {
  const result = await chrome.storage.local.get('collector:status');
  return result['collector:status'] ?? null;
}

// Hämtar sparade detaljer och lineups för matcherna. Returnerar Map matchId -> { match, lineup, analysis, at }
export async function getMatchDetails(matchIds) {
  if (matchIds.length === 0) return new Map();
  const keys = matchIds.flatMap(id => [`match:${id}`, `lineup:${id}`]);
  const stored = await chrome.storage.local.get(keys);
  const details = new Map();

  for (const id of matchIds) {
    const entry = stored[`match:${id}`];
    if (!entry?.match) continue;

    const cacheKey = `${id}|${entry.at}`;
    let analysis = analysisCache.get(cacheKey);
    if (!analysis) {
      fillEventTeams(entry.match);
      analysis = analyzeMatch(entry.match);
      analysisCache.set(cacheKey, analysis);
    }
    details.set(id, {
      match: entry.match,
      lineup: stored[`lineup:${id}`]?.lineup ?? null,
      analysis,
      at: entry.at
    });
  }
  return details;
}

// Spelade matcher i listan vars detaljer saknas eller sparades innan matchen var färdigspelad
export function missingDetails(matches, details) {
  return matches.filter(m => {
    if (!isPlayed(m)) return false;
    const d = details.get(m.MatchID);
    return !d || !isPlayed(d.match);
  });
}
