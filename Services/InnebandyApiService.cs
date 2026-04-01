using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using InnebandyStats.Models;
using InnebandyStats.Models.Api;

namespace InnebandyStats.Services;

public class InnebandyApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<InnebandyApiService> _logger;
    private readonly IMemoryCache _cache;
    private string? _token;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public InnebandyApiService(HttpClient httpClient, ILogger<InnebandyApiService> logger, IMemoryCache cache)
    {
        _httpClient = httpClient;
        _logger = logger;
        _cache = cache;
    }

    private async Task EnsureTokenAsync()
    {
        if (!string.IsNullOrEmpty(_token))
            return;

        _logger.LogInformation("Hämtar token från startkit API...");

        var response = await _httpClient.GetAsync("https://api.innebandy.se/StatsAppApi/api/startkit");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("accessToken", out var tokenProp))
        {
            _token = tokenProp.GetString()
                ?? throw new Exception("accessToken var null i startkit-svaret.");
        }
        else
        {
            throw new Exception("Kunde inte hitta accessToken i startkit-svaret.");
        }

        _logger.LogInformation("Token hämtad.");
    }

    private async Task<HttpRequestMessage> CreateAuthorizedRequest(string url)
    {
        await EnsureTokenAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    public async Task<List<Match>> GetMatchesAsync(int competitionId)
    {
        var cacheKey = $"matches_{competitionId}";
        if (_cache.TryGetValue(cacheKey, out List<Match>? cachedMatches) && cachedMatches != null)
            return cachedMatches;

        var request = await CreateAuthorizedRequest(
            $"https://api.innebandy.se/v2/api/competitions/{competitionId}/matches");

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var matches = JsonSerializer.Deserialize<List<Match>>(json, JsonOptions) ?? new List<Match>();
        _cache.Set(cacheKey, matches, TimeSpan.FromMinutes(5));
        return matches;
    }

    public async Task<Match?> GetMatchDetailsAsync(int matchId)
    {
        var cacheKey = $"matchdetails_{matchId}";
        if (_cache.TryGetValue(cacheKey, out Match? cachedDetail) && cachedDetail != null)
            return cachedDetail;

        var request = await CreateAuthorizedRequest(
            $"https://api.innebandy.se/v2/api/matches/{matchId}");

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var match = JsonSerializer.Deserialize<Match>(json, JsonOptions);
        if (match != null)
            _cache.Set(cacheKey, match, TimeSpan.FromMinutes(30));
        return match;
    }

    public async Task<Lineup?> GetLineupAsync(int matchId)
    {
        var request = await CreateAuthorizedRequest(
            $"https://api.innebandy.se/v2/api/matches/{matchId}/lineups");

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Kunde inte hämta lineup för match {MatchId}: {Status}", matchId, response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<Lineup>(json, JsonOptions);
    }

    public async Task<Player?> GetPlayerAsync(int playerId)
    {
        var request = await CreateAuthorizedRequest(
            $"https://api.innebandy.se/v2/api/players/{playerId}");

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Kunde inte hämta spelare {PlayerId}: {Status}", playerId, response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<Player>(json, JsonOptions);
    }

    public async Task<List<PlayerStanding>> GetStandingsAsync(int competitionId)
    {
        var cacheKey = $"standings_{competitionId}";

        if (_cache.TryGetValue(cacheKey, out List<PlayerStanding>? cached) && cached != null)
        {
            _logger.LogInformation("Hämtar poängliga för tävling {CompetitionId} från cache.", competitionId);
            return cached;
        }

        // 1. Hämta alla matcher
        _logger.LogInformation("Hämtar matcher för tävling {CompetitionId}...", competitionId);
        var matches = await GetMatchesAsync(competitionId);

        if (matches.Count == 0)
            return new List<PlayerStanding>();

        // 2. Hämta matchdetaljer och lineups för spelade matcher
        var playedMatches = matches.Where(m => m.MatchStatus == 4).ToList();
        _logger.LogInformation("Hämtar detaljer och lineups för {Count} spelade matcher...", playedMatches.Count);

        var matchDetails = new List<Match>();
        var lineups = new List<Lineup>();

        foreach (var batch in playedMatches.Chunk(5))
        {
            var detailTasks = batch.Select(m => GetMatchDetailsAsync(m.MatchID));
            var lineupTasks = batch.Select(m => GetLineupAsync(m.MatchID));

            var detailResults = await Task.WhenAll(detailTasks);
            var lineupResults = await Task.WhenAll(lineupTasks);

            matchDetails.AddRange(detailResults.Where(r => r != null)!);
            lineups.AddRange(lineupResults.Where(r => r != null)!);
        }

        // 3. Bygg spelarinfo från lineups (ålder, födelseår, lag, matchdeltagande)
        // Nyckla på (PlayerID, Team) för att separera spelare som spelar i flera lag
        var playerStats = new Dictionary<(int PlayerId, string Team), PlayerStanding>();

        foreach (var lineup in lineups)
        {
            var homeTeamName = lineup.HomeTeam.Trim();
            var awayTeamName = lineup.AwayTeam.Trim();

            foreach (var p in lineup.HomeTeamPlayers)
            {
                EnsurePlayerByTeam(playerStats, p.PlayerID, p.Name, homeTeamName);
                playerStats[(p.PlayerID, homeTeamName)].Matches++;
                if (p.Age > 0) playerStats[(p.PlayerID, homeTeamName)].Age = p.Age;
                if (p.BirthYear > 0) playerStats[(p.PlayerID, homeTeamName)].BirthYear = p.BirthYear;
            }

            foreach (var p in lineup.AwayTeamPlayers)
            {
                EnsurePlayerByTeam(playerStats, p.PlayerID, p.Name, awayTeamName);
                playerStats[(p.PlayerID, awayTeamName)].Matches++;
                if (p.Age > 0) playerStats[(p.PlayerID, awayTeamName)].Age = p.Age;
                if (p.BirthYear > 0) playerStats[(p.PlayerID, awayTeamName)].BirthYear = p.BirthYear;
            }
        }

        // 4. Samla ihop mål, assist och utvisningar från matchdetaljer
        foreach (var match in matchDetails)
        {
            if (match.Events == null) continue;

            foreach (var evt in match.Events)
            {
                // Mål (MatchEventTypeID = 1)
                if (evt.MatchEventTypeID == 1 && evt.PlayerID > 0)
                {
                    var teamName = evt.MatchTeamName?.Trim() ?? "";
                    EnsurePlayerByTeam(playerStats, evt.PlayerID, evt.PlayerName, teamName);
                    playerStats[(evt.PlayerID, teamName)].Goals++;

                    // Assist
                    if (evt.PlayerAssistID > 0)
                    {
                        EnsurePlayerByTeam(playerStats, evt.PlayerAssistID, evt.PlayerAssistName, teamName);
                        playerStats[(evt.PlayerAssistID, teamName)].Assists++;
                    }
                }

                // Utvisning (MatchEventTypeID = 2)
                if (evt.MatchEventTypeID == 2 && evt.PlayerID > 0)
                {
                    var teamName = evt.MatchTeamName?.Trim() ?? "";
                    EnsurePlayerByTeam(playerStats, evt.PlayerID, evt.PlayerName, teamName);
                    playerStats[(evt.PlayerID, teamName)].PenaltyMinutes += 2;
                }
            }
        }

        // 5. Hämta födelseår från player-API (lineups saknar BirthYear)
        var playerIds = playerStats.Keys.Select(k => k.PlayerId).Distinct().ToList();
        _logger.LogInformation("Hämtar spelardetaljer för {Count} spelare...", playerIds.Count);

        foreach (var batch in playerIds.Chunk(10))
        {
            var tasks = batch.Select(id => GetPlayerAsync(id));
            var results = await Task.WhenAll(tasks);

            foreach (var player in results.Where(p => p != null))
            {
                // Uppdatera alla poster för denna spelare (kan finnas i flera lag)
                foreach (var key in playerStats.Keys.Where(k => k.PlayerId == player!.PlayerID))
                {
                    if (player!.Age > 0) playerStats[key].Age = player.Age;
                    if (player.BirthYear > 0) playerStats[key].BirthYear = player.BirthYear;
                    if (!string.IsNullOrEmpty(player.Name))
                        playerStats[key].Name = player.Name;
                }
            }
        }

        var standings = playerStats.Values.ToList();

        // Spara i cache i 10 minuter
        _cache.Set(cacheKey, standings, TimeSpan.FromMinutes(10));
        _logger.LogInformation("Poängliga cachad för tävling {CompetitionId} ({Count} spelare).", competitionId, standings.Count);

        return standings;
    }

    public async Task<List<TeamTableEntry>> GetSeriesTableAsync(int competitionId)
    {
        var cacheKey = $"seriestable_{competitionId}";
        if (_cache.TryGetValue(cacheKey, out List<TeamTableEntry>? cached) && cached != null)
            return cached;

        var matches = await GetMatchesAsync(competitionId);
        var playedMatches = matches
            .Where(m => m.MatchStatus == 4 && m.GoalsHomeTeam.HasValue && m.GoalsAwayTeam.HasValue)
            .ToList();

        var teams = new Dictionary<int, TeamTableEntry>();

        void EnsureTeam(int teamId, string teamName)
        {
            if (!teams.ContainsKey(teamId))
                teams[teamId] = new TeamTableEntry { TeamID = teamId, TeamName = teamName.Trim() };
        }

        foreach (var match in playedMatches)
        {
            EnsureTeam(match.HomeTeamID, match.HomeTeam);
            EnsureTeam(match.AwayTeamID, match.AwayTeam);

            var home = teams[match.HomeTeamID];
            var away = teams[match.AwayTeamID];
            int homeGoals = match.GoalsHomeTeam!.Value;
            int awayGoals = match.GoalsAwayTeam!.Value;

            home.Played++;
            away.Played++;
            home.GoalsFor += homeGoals;
            home.GoalsAgainst += awayGoals;
            away.GoalsFor += awayGoals;
            away.GoalsAgainst += homeGoals;

            if (homeGoals > awayGoals) { home.Wins++; away.Losses++; }
            else if (homeGoals < awayGoals) { away.Wins++; home.Losses++; }
            else { home.Draws++; away.Draws++; }
        }

        var table = teams.Values
            .OrderByDescending(t => t.Points)
            .ThenByDescending(t => t.GoalDiff)
            .ThenByDescending(t => t.GoalsFor)
            .ThenBy(t => t.TeamName)
            .ToList();

        _cache.Set(cacheKey, table, TimeSpan.FromMinutes(10));
        return table;
    }

    public async Task<string> GetCompetitionNameAsync(int competitionId)
    {
        var cacheKey = $"compname_{competitionId}";
        if (_cache.TryGetValue(cacheKey, out string? name) && name != null)
            return name;

        var matches = await GetMatchesAsync(competitionId);
        var compName = matches.FirstOrDefault()?.CompetitionName.Trim() ?? "";
        _cache.Set(cacheKey, compName, TimeSpan.FromMinutes(10));
        return compName;
    }

    public async Task<List<Season>> GetSeasonsAsync()
    {
        var cacheKey = "seasons";

        if (_cache.TryGetValue(cacheKey, out List<Season>? cached) && cached != null)
            return cached;

        var request = await CreateAuthorizedRequest("https://api.innebandy.se/v2/api/seasons/");
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var seasons = JsonSerializer.Deserialize<List<Season>>(json, JsonOptions) ?? new List<Season>();

        _cache.Set(cacheKey, seasons, TimeSpan.FromHours(1));
        return seasons;
    }

    public async Task<List<Federation>> GetFederationsAsync()
    {
        var cacheKey = "federations";

        if (_cache.TryGetValue(cacheKey, out List<Federation>? cached) && cached != null)
            return cached;

        var request = await CreateAuthorizedRequest("https://api.innebandy.se/v2/api/federations/");
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var federations = JsonSerializer.Deserialize<List<Federation>>(json, JsonOptions) ?? new List<Federation>();

        federations = federations.OrderBy(f => f.Name).ToList();

        _cache.Set(cacheKey, federations, TimeSpan.FromHours(1));
        return federations;
    }

    public async Task<List<Competition>> GetCompetitionsAsync(int seasonId = 43, int federationId = 8)
    {
        var cacheKey = $"competitions_{seasonId}_{federationId}";

        if (_cache.TryGetValue(cacheKey, out List<Competition>? cached) && cached != null)
            return cached;

        var request = await CreateAuthorizedRequest(
            $"https://api.innebandy.se/v2/api/seasons/{seasonId}/federations/{federationId}/competitions");

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var competitions = JsonSerializer.Deserialize<List<Competition>>(json, JsonOptions) ?? new List<Competition>();

        // Sortera på namn
        competitions = competitions.OrderBy(c => c.Name).ToList();

        _cache.Set(cacheKey, competitions, TimeSpan.FromMinutes(30));
        return competitions;
    }

    public async Task<(Match? MatchInfo, List<PlayerStanding> HomeStandings, List<PlayerStanding> AwayStandings)> GetMatchStandingsAsync(int matchId)
    {
        var cacheKey = $"matchstandings_{matchId}";
        if (_cache.TryGetValue(cacheKey, out (Match?, List<PlayerStanding>, List<PlayerStanding>) cached))
            return cached;

        var matchInfo = await GetMatchDetailsAsync(matchId);
        if (matchInfo == null)
            return (null, new List<PlayerStanding>(), new List<PlayerStanding>());

        var lineup = await GetLineupAsync(matchId);

        var homeTeamName = matchInfo.HomeTeam.Trim();
        var awayTeamName = matchInfo.AwayTeam.Trim();

        var playerStats = new Dictionary<int, PlayerStanding>();
        var isHomePlayer = new Dictionary<int, bool>();

        if (lineup != null)
        {
            foreach (var p in lineup.HomeTeamPlayers)
            {
                EnsurePlayer(playerStats, p.PlayerID, p.Name, homeTeamName);
                playerStats[p.PlayerID].Matches = 1;
                if (p.Age > 0) playerStats[p.PlayerID].Age = p.Age;
                if (p.BirthYear > 0) playerStats[p.PlayerID].BirthYear = p.BirthYear;
                isHomePlayer[p.PlayerID] = true;
            }
            foreach (var p in lineup.AwayTeamPlayers)
            {
                EnsurePlayer(playerStats, p.PlayerID, p.Name, awayTeamName);
                playerStats[p.PlayerID].Matches = 1;
                if (p.Age > 0) playerStats[p.PlayerID].Age = p.Age;
                if (p.BirthYear > 0) playerStats[p.PlayerID].BirthYear = p.BirthYear;
                isHomePlayer[p.PlayerID] = false;
            }
        }

        if (matchInfo.Events != null)
        {
            foreach (var evt in matchInfo.Events)
            {
                bool evtIsHome = evt.IsHomeTeam == true;
                var teamName = evtIsHome ? homeTeamName : awayTeamName;

                if (evt.MatchEventTypeID == 1 && evt.PlayerID > 0)
                {
                    EnsurePlayer(playerStats, evt.PlayerID, evt.PlayerName, teamName);
                    if (!isHomePlayer.ContainsKey(evt.PlayerID)) isHomePlayer[evt.PlayerID] = evtIsHome;
                    playerStats[evt.PlayerID].Goals++;

                    if (evt.PlayerAssistID > 0)
                    {
                        EnsurePlayer(playerStats, evt.PlayerAssistID, evt.PlayerAssistName, teamName);
                        if (!isHomePlayer.ContainsKey(evt.PlayerAssistID)) isHomePlayer[evt.PlayerAssistID] = evtIsHome;
                        playerStats[evt.PlayerAssistID].Assists++;
                    }
                }

                if (evt.MatchEventTypeID == 2 && evt.PlayerID > 0)
                {
                    EnsurePlayer(playerStats, evt.PlayerID, evt.PlayerName, teamName);
                    if (!isHomePlayer.ContainsKey(evt.PlayerID)) isHomePlayer[evt.PlayerID] = evtIsHome;
                    playerStats[evt.PlayerID].PenaltyMinutes += 2;
                }
            }
        }

        var homeStandings = playerStats
            .Where(kv => !isHomePlayer.ContainsKey(kv.Key) || isHomePlayer[kv.Key])
            .Select(kv => kv.Value)
            .OrderByDescending(p => p.Points).ThenByDescending(p => p.Goals).ThenBy(p => p.Name)
            .ToList();

        var awayStandings = playerStats
            .Where(kv => isHomePlayer.ContainsKey(kv.Key) && !isHomePlayer[kv.Key])
            .Select(kv => kv.Value)
            .OrderByDescending(p => p.Points).ThenByDescending(p => p.Goals).ThenBy(p => p.Name)
            .ToList();

        var result = (matchInfo, homeStandings, awayStandings);
        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(10));
        return result;
    }

    public async Task<TeamAnalysisViewModel> GetTeamAnalysisAsync(int competitionId, string teamName)
    {
        var cacheKey = $"teamanalysis_{competitionId}_{teamName}";
        if (_cache.TryGetValue(cacheKey, out TeamAnalysisViewModel? cachedAnalysis) && cachedAnalysis != null)
            return cachedAnalysis;

        // Parallel fetch of base data (all cached individually)
        var matchesTask = GetMatchesAsync(competitionId);
        var standingsTask = GetStandingsAsync(competitionId);
        var tableTask = GetSeriesTableAsync(competitionId);
        var compNameTask = GetCompetitionNameAsync(competitionId);
        await Task.WhenAll(matchesTask, standingsTask, tableTask, compNameTask);

        var allMatches = await matchesTask;
        var allStandings = await standingsTask;
        var seriesTable = await tableTask;
        var compName = await compNameTask;

        // Filter and sort team's matches
        var teamMatches = allMatches
            .Where(m => m.HomeTeam.Trim() == teamName || m.AwayTeam.Trim() == teamName)
            .OrderByDescending(m => m.MatchDateTime)
            .ToList();

        var played = teamMatches.Where(m => m.MatchStatus == 4 && m.GoalsHomeTeam.HasValue).ToList();
        var upcoming = teamMatches.Where(m => m.MatchStatus != 4).OrderBy(m => m.MatchDateTime).ToList();

        // Table position
        var tableEntry = seriesTable.FirstOrDefault(t => t.TeamName == teamName);
        var tableRank = seriesTable.FindIndex(t => t.TeamName == teamName) + 1;

        // Home/away stats, averages, streaks
        int homePlayed = 0, homeWins = 0, homeDraws = 0, homeLosses = 0;
        int awayPlayed = 0, awayWins = 0, awayDraws = 0, awayLosses = 0;
        int totalGF = 0, totalGA = 0;
        int unbeatenStreak = 0, winStreak = 0;
        bool unbeatenBroken = false, winBroken = false;

        foreach (var m in played)
        {
            bool isHome = m.HomeTeam.Trim() == teamName;
            int gf = isHome ? m.GoalsHomeTeam!.Value : m.GoalsAwayTeam!.Value;
            int ga = isHome ? m.GoalsAwayTeam!.Value : m.GoalsHomeTeam!.Value;
            totalGF += gf; totalGA += ga;
            bool won = gf > ga, drew = gf == ga;

            if (isHome) { homePlayed++; if (won) homeWins++; else if (drew) homeDraws++; else homeLosses++; }
            else { awayPlayed++; if (won) awayWins++; else if (drew) awayDraws++; else awayLosses++; }

            if (!unbeatenBroken) { if (gf >= ga) unbeatenStreak++; else unbeatenBroken = true; }
            if (!winBroken) { if (won) winStreak++; else winBroken = true; }
        }

        // Form players: last 3 match events
        const int formCount = 3;
        var lastPlayed = played.Take(formCount).ToList();
        var formDict = new Dictionary<int, FormPlayer>();

        if (lastPlayed.Any())
        {
            var detailTasks = lastPlayed.Select(m => GetMatchDetailsAsync(m.MatchID));
            var details = await Task.WhenAll(detailTasks);

            foreach (var detail in details.Where(d => d?.Events != null))
            {
                bool detailIsHome = detail!.HomeTeam.Trim() == teamName;
                foreach (var evt in detail.Events!)
                {
                    if (evt.IsHomeTeam != detailIsHome) continue;
                    if (evt.MatchEventTypeID == 1 && evt.PlayerID > 0)
                    {
                        if (!formDict.TryGetValue(evt.PlayerID, out var fp))
                            formDict[evt.PlayerID] = fp = new FormPlayer { PlayerID = evt.PlayerID, Name = evt.PlayerName };
                        fp.FormGoals++;

                        if (evt.PlayerAssistID > 0)
                        {
                            if (!formDict.TryGetValue(evt.PlayerAssistID, out var fa))
                                formDict[evt.PlayerAssistID] = fa = new FormPlayer { PlayerID = evt.PlayerAssistID, Name = evt.PlayerAssistName };
                            fa.FormAssists++;
                        }
                    }
                }
            }
        }

        var seasonLookup = allStandings.Where(p => p.Team == teamName).ToDictionary(p => p.PlayerID);
        foreach (var fp in formDict.Values)
            fp.SeasonStats = seasonLookup.TryGetValue(fp.PlayerID, out var sp) ? sp : null;

        var formPlayers = formDict.Values
            .OrderByDescending(fp => fp.FormPoints).ThenByDescending(fp => fp.FormGoals)
            .ToList();

        // Build match result lists
        static TeamMatchResult ToResult(Match m, string teamName) {
            bool isHome = m.HomeTeam.Trim() == teamName;
            return new TeamMatchResult {
                MatchID = m.MatchID,
                MatchDateTime = m.MatchDateTime,
                Opponent = isHome ? m.AwayTeam.Trim() : m.HomeTeam.Trim(),
                IsHome = isHome,
                GoalsFor = isHome ? m.GoalsHomeTeam : m.GoalsAwayTeam,
                GoalsAgainst = isHome ? m.GoalsAwayTeam : m.GoalsHomeTeam,
                MatchStatus = m.MatchStatus,
                RoundName = m.RoundName,
                Round = m.Round
            };
        }

        var topPlayers = allStandings
            .Where(p => p.Team == teamName)
            .OrderByDescending(p => p.Points).ThenByDescending(p => p.Goals).ThenBy(p => p.Name)
            .ToList();

        int totalPlayed = homePlayed + awayPlayed;
        var result = new TeamAnalysisViewModel
        {
            CompetitionId = competitionId,
            CompetitionName = compName,
            TeamName = teamName,
            TableRank = tableRank,
            TableEntry = tableEntry,
            RecentMatches = played.Take(5).Select(m => ToResult(m, teamName)).ToList(),
            UpcomingMatches = upcoming.Take(3).Select(m => ToResult(m, teamName)).ToList(),
            TopPlayers = topPlayers,
            FormPlayers = formPlayers,
            AvgGoalsFor = totalPlayed > 0 ? Math.Round((double)totalGF / totalPlayed, 1) : 0,
            AvgGoalsAgainst = totalPlayed > 0 ? Math.Round((double)totalGA / totalPlayed, 1) : 0,
            HomePlayed = homePlayed, HomeWins = homeWins, HomeDraws = homeDraws, HomeLosses = homeLosses,
            AwayPlayed = awayPlayed, AwayWins = awayWins, AwayDraws = awayDraws, AwayLosses = awayLosses,
            CurrentUnbeatenStreak = unbeatenStreak,
            CurrentWinStreak = winStreak,
            FormMatchCount = formCount
        };

        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(10));
        return result;
    }

    public async Task<List<TeamSearchResult>> SearchTeamAsync(string query, int seasonId, int federationId)
    {
        var cacheKey = $"teamsearch_{seasonId}_{federationId}_{query.ToLower().Trim()}";
        if (_cache.TryGetValue(cacheKey, out List<TeamSearchResult>? cached) && cached != null)
            return cached;

        var competitions = await GetCompetitionsAsync(seasonId, federationId);
        var results = new List<TeamSearchResult>();
        var lowerQuery = query.ToLower().Trim();

        // Hämta matcher för alla tävlingar parallellt i batchar
        foreach (var batch in competitions.Chunk(10))
        {
            var tasks = batch.Select(async c =>
            {
                try
                {
                    var matches = await GetMatchesAsync(c.CompetitionID);
                    var teams = matches
                        .SelectMany(m => new[]
                        {
                            new { Id = m.HomeTeamID, Name = m.HomeTeam.Trim() },
                            new { Id = m.AwayTeamID, Name = m.AwayTeam.Trim() }
                        })
                        .Where(t => !string.IsNullOrEmpty(t.Name))
                        .DistinctBy(t => t.Id)
                        .Where(t => t.Name.ToLower().Contains(lowerQuery))
                        .ToList();

                    return teams.Select(t => new TeamSearchResult
                    {
                        TeamName = t.Name,
                        TeamID = t.Id,
                        CompetitionID = c.CompetitionID,
                        CompetitionName = c.Name
                    }).ToList();
                }
                catch
                {
                    return new List<TeamSearchResult>();
                }
            });

            var batchResults = await Task.WhenAll(tasks);
            results.AddRange(batchResults.SelectMany(r => r));
        }

        results = results.OrderBy(r => r.TeamName).ThenBy(r => r.CompetitionName).ToList();
        _cache.Set(cacheKey, results, TimeSpan.FromMinutes(10));
        return results;
    }

    private static void EnsurePlayer(Dictionary<int, PlayerStanding> dict, int playerId, string name, string team)
    {
        if (!dict.ContainsKey(playerId))
        {
            dict[playerId] = new PlayerStanding
            {
                PlayerID = playerId,
                Name = name,
                Team = team
            };
        }
    }

    private static void EnsurePlayerByTeam(Dictionary<(int PlayerId, string Team), PlayerStanding> dict, int playerId, string name, string team)
    {
        var key = (playerId, team);
        if (!dict.ContainsKey(key))
        {
            dict[key] = new PlayerStanding
            {
                PlayerID = playerId,
                Name = name,
                Team = team
            };
        }
    }
}
