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

    private const string StartKitUrl = "https://api.innebandy.se/StatsAppApi/api/startkit";
    private const string DefaultApiRoot = "https://api.innebandy.se/v2/api/public/";
    private const string StartKitCacheKey = "api_startkit";

    // Minsta tid ett API-svar återanvänds innan samma endpoint anropas igen
    private static readonly TimeSpan ApiCacheDuration = TimeSpan.FromMinutes(10);
    // Token gäller 30 min hos innebandy.se
    private static readonly TimeSpan TokenCacheDuration = TimeSpan.FromMinutes(20);
    private static readonly object CacheLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private record StartKit(string Token, string ApiRoot);

    public InnebandyApiService(HttpClient httpClient, ILogger<InnebandyApiService> logger, IMemoryCache cache)
    {
        _httpClient = httpClient;
        _logger = logger;
        _cache = cache;
    }

    // Cachar resultatet under ttl. Samtidiga anrop med samma nyckel delar på samma hämtning,
    // och misslyckade hämtningar cachas inte.
    private async Task<T> GetOrFetchAsync<T>(string cacheKey, TimeSpan ttl, Func<Task<T>> fetch)
    {
        Lazy<Task<T>> lazy;
        lock (CacheLock)
        {
            lazy = _cache.GetOrCreate(cacheKey, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = ttl;
                return new Lazy<Task<T>>(fetch);
            })!;
        }

        try
        {
            return await lazy.Value;
        }
        catch
        {
            _cache.Remove(cacheKey);
            throw;
        }
    }

    private Task<StartKit> GetStartKitAsync() =>
        GetOrFetchAsync(StartKitCacheKey, TokenCacheDuration, async () =>
        {
            _logger.LogInformation("Hämtar token från startkit API...");

            var response = await _httpClient.GetAsync(StartKitUrl);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("accessToken", out var tokenProp))
                throw new Exception("Kunde inte hitta accessToken i startkit-svaret.");

            var token = tokenProp.GetString()
                ?? throw new Exception("accessToken var null i startkit-svaret.");

            var apiRoot = DefaultApiRoot;
            if (doc.RootElement.TryGetProperty("apiRoot", out var rootProp)
                && rootProp.GetString() is { Length: > 0 } root)
            {
                apiRoot = root.EndsWith('/') ? root : root + "/";
            }

            _logger.LogInformation("Token hämtad.");
            return new StartKit(token, apiRoot);
        });

    // Hämtar rå JSON från API:t, cachad per sökväg (endpoint + parametrar)
    private Task<string> GetApiJsonAsync(string path, TimeSpan? ttl = null) =>
        GetOrFetchAsync($"api_{path}", ttl ?? ApiCacheDuration, async () =>
        {
            var response = await SendAuthorizedAsync(path);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Token kan ha gått ut i förtid – hämta ny och försök igen
                _cache.Remove(StartKitCacheKey);
                response = await SendAuthorizedAsync(path);
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        });

    private async Task<HttpResponseMessage> SendAuthorizedAsync(string path)
    {
        var startKit = await GetStartKitAsync();
        _logger.LogDebug("API-anrop: {Path}", path);
        var request = new HttpRequestMessage(HttpMethod.Get, startKit.ApiRoot + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", startKit.Token);
        return await _httpClient.SendAsync(request);
    }

    public async Task<List<Match>> GetMatchesAsync(int competitionId)
    {
        var json = await GetApiJsonAsync($"competitions/{competitionId}/matches");
        return JsonSerializer.Deserialize<List<Match>>(json, JsonOptions) ?? new List<Match>();
    }

    public async Task<Match?> GetMatchDetailsAsync(int matchId)
    {
        var json = await GetApiJsonAsync($"matches/{matchId}");
        var match = JsonSerializer.Deserialize<Match>(json, JsonOptions);
        if (match != null)
            FillEventTeams(match);
        return match;
    }

    // Det publika API:t skickar bara MatchTeamID på händelser, så härled lag från matchen
    private static void FillEventTeams(Match match)
    {
        if (match.Events == null) return;

        foreach (var evt in match.Events)
        {
            if (evt.MatchTeamID == 0) continue;

            if (evt.MatchTeamID == match.HomeMatchTeamID)
            {
                evt.IsHomeTeam ??= true;
                if (string.IsNullOrEmpty(evt.MatchTeamName)) evt.MatchTeamName = match.HomeTeam;
                evt.MatchTeamShortName ??= match.HomeTeamShortName;
            }
            else if (evt.MatchTeamID == match.AwayMatchTeamID)
            {
                evt.IsHomeTeam ??= false;
                if (string.IsNullOrEmpty(evt.MatchTeamName)) evt.MatchTeamName = match.AwayTeam;
                evt.MatchTeamShortName ??= match.AwayTeamShortName;
            }
        }
    }

    public async Task<Lineup?> GetLineupAsync(int matchId)
    {
        try
        {
            var json = await GetApiJsonAsync($"matches/{matchId}/lineups");
            var lineup = JsonSerializer.Deserialize<Lineup>(json, JsonOptions);
            if (lineup != null)
            {
                // Ospelade matcher har null i stället för tomma listor
                lineup.HomeTeamPlayers ??= new List<LineupPlayer>();
                lineup.AwayTeamPlayers ??= new List<LineupPlayer>();
                foreach (var p in lineup.HomeTeamPlayers.Concat(lineup.AwayTeamPlayers))
                {
                    p.Name ??= "";
                    p.Position ??= "";
                }
            }
            return lineup;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Kunde inte hämta lineup för match {MatchId}: {Status}", matchId, ex.StatusCode);
            return null;
        }
    }

    public async Task<Player?> GetPlayerAsync(int playerId)
    {
        try
        {
            var json = await GetApiJsonAsync($"players/{playerId}");
            return JsonSerializer.Deserialize<Player>(json, JsonOptions);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Kunde inte hämta spelare {PlayerId}: {Status}", playerId, ex.StatusCode);
            return null;
        }
    }

    // ---- Seriedata: allt underlag för en serie, hämtas en gång och delas av alla vyer ----

    private const int PlayedStatus = 4;

    public Task<CompetitionData> GetCompetitionDataAsync(int competitionId) =>
        GetOrFetchAsync($"competitiondata_{competitionId}", ApiCacheDuration, async () =>
        {
            var matches = await GetMatchesAsync(competitionId);
            var data = new CompetitionData
            {
                CompetitionId = competitionId,
                CompetitionName = matches.FirstOrDefault()?.CompetitionName.Trim() ?? "",
                Matches = matches
            };

            var played = matches.Where(m => m.MatchStatus == PlayedStatus).ToList();
            _logger.LogInformation("Hämtar detaljer och lineups för {Count} spelade matcher...", played.Count);

            foreach (var batch in played.Chunk(5))
            {
                var results = await Task.WhenAll(batch.Select(async m =>
                {
                    var detailTask = GetMatchDetailsAsync(m.MatchID);
                    var lineupTask = GetLineupAsync(m.MatchID);
                    var detail = await detailTask;
                    var lineup = await lineupTask;

                    return detail == null
                        ? null
                        : new PlayedMatch { Match = detail, Lineup = lineup, Analysis = MatchAnalyzer.Analyze(detail) };
                }));

                data.Played.AddRange(results.OfType<PlayedMatch>());
            }

            data.Played = data.Played.OrderBy(p => p.Match.MatchDateTime).ToList();
            return data;
        });

    // Varje spelares insats i en match, från lineup och händelser
    public static List<PlayerMatchLine> BuildPlayerLines(PlayedMatch pm)
    {
        var match = pm.Match;
        var goals = pm.Analysis.Goals.ToList();
        var homeGoals = match.GoalsHomeTeam ?? goals.Count(g => g.IsHome);
        var awayGoals = match.GoalsAwayTeam ?? goals.Count(g => !g.IsHome);
        var hasLineup = pm.Lineup != null && (pm.Lineup.HomeTeamPlayers.Count > 0 || pm.Lineup.AwayTeamPlayers.Count > 0);
        var lines = new Dictionary<int, PlayerMatchLine>();

        PlayerMatchLine Ensure(int playerId, string name, bool isHome)
        {
            if (!lines.TryGetValue(playerId, out var line))
            {
                lines[playerId] = line = new PlayerMatchLine
                {
                    MatchID = match.MatchID,
                    MatchDateTime = match.MatchDateTime,
                    PlayerID = playerId,
                    Name = (name ?? "").Trim(),
                    IsHome = isHome,
                    Team = isHome ? pm.HomeTeam : pm.AwayTeam,
                    Opponent = isHome ? pm.AwayTeam : pm.HomeTeam,
                    GoalsFor = isHome ? homeGoals : awayGoals,
                    GoalsAgainst = isHome ? awayGoals : homeGoals,
                    Played = !hasLineup
                };
            }
            return line;
        }

        if (pm.Lineup != null)
        {
            foreach (var (players, isHome) in new[] { (pm.Lineup.HomeTeamPlayers, true), (pm.Lineup.AwayTeamPlayers, false) })
            {
                foreach (var p in players.Where(p => p.PlayerID > 0))
                {
                    var line = Ensure(p.PlayerID, p.Name, isHome);
                    line.Played = true;
                    line.ShirtNo = p.ShirtNo;
                    line.Position = p.Position;
                    line.Captain = p.Captain;
                }
            }
        }

        foreach (var e in pm.Analysis.Events)
        {
            if (e.IsGoal && e.PlayerID > 0)
            {
                var scorer = Ensure(e.PlayerID, e.PlayerName, e.IsHome);
                scorer.Goals++;
                if (e.Strength == GoalStrength.PowerPlay) scorer.PowerPlayGoals++;
                if (e.Strength == GoalStrength.ShortHanded) scorer.ShortHandedGoals++;
                if (e.IsGameWinner) scorer.GameWinningGoals++;

                if (e.AssistID > 0)
                    Ensure(e.AssistID, e.AssistName, e.IsHome).Assists++;
            }
            else if (e.Kind == TimelineEventKind.Penalty && e.PlayerID > 0)
            {
                Ensure(e.PlayerID, e.PlayerName, e.IsHome).PenaltyMinutes += e.PenaltyMinutes;
            }
        }

        return lines.Values.ToList();
    }

    public Task<List<PlayerStanding>> GetStandingsAsync(int competitionId) =>
        GetOrFetchAsync($"standings_{competitionId}", ApiCacheDuration, async () =>
        {
            var data = await GetCompetitionDataAsync(competitionId);

            // Nyckla på (PlayerID, Team) för att separera spelare som spelar i flera lag
            var standings = data.Played
                .SelectMany(BuildPlayerLines)
                .GroupBy(l => (l.PlayerID, l.Team))
                .Select(g => new PlayerStanding
                {
                    PlayerID = g.Key.PlayerID,
                    Team = g.Key.Team,
                    Name = g.Last().Name,
                    Position = MostCommon(g.Select(l => l.Position)),
                    Matches = g.Count(l => l.Played),
                    Goals = g.Sum(l => l.Goals),
                    Assists = g.Sum(l => l.Assists),
                    PenaltyMinutes = g.Sum(l => l.PenaltyMinutes),
                    PowerPlayGoals = g.Sum(l => l.PowerPlayGoals),
                    ShortHandedGoals = g.Sum(l => l.ShortHandedGoals),
                    GameWinningGoals = g.Sum(l => l.GameWinningGoals)
                })
                .ToList();

            // Ålder och födelseår finns bara i spelar-API:t
            var playerIds = standings.Select(s => s.PlayerID).Distinct().ToList();
            _logger.LogInformation("Hämtar spelardetaljer för {Count} spelare...", playerIds.Count);

            var byPlayer = standings.ToLookup(s => s.PlayerID);
            foreach (var batch in playerIds.Chunk(10))
            {
                var results = await Task.WhenAll(batch.Select(GetPlayerAsync));
                foreach (var player in results.OfType<Player>())
                {
                    foreach (var standing in byPlayer[player.PlayerID])
                    {
                        if (player.Age > 0) standing.Age = player.Age;
                        if (player.BirthYear > 0) standing.BirthYear = player.BirthYear;
                        if (!string.IsNullOrEmpty(player.Name)) standing.Name = player.Name;
                        if (string.IsNullOrEmpty(standing.Position)) standing.Position = player.Position;
                    }
                }
            }

            _logger.LogInformation("Poängliga klar för tävling {CompetitionId} ({Count} spelare).", competitionId, standings.Count);
            return standings;
        });

    private static string MostCommon(IEnumerable<string> values) =>
        values.Where(v => !string.IsNullOrEmpty(v))
            .GroupBy(v => v)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? "";

    // ---- Tabeller ----

    // scope: all, home (bara hemmamatcher) eller away (bara bortamatcher)
    public static List<TeamTableEntry> BuildTable(IEnumerable<Match> matches, string scope = "all")
    {
        var teams = new Dictionary<int, TeamTableEntry>();

        TeamTableEntry Ensure(int teamId, string teamName)
        {
            if (!teams.TryGetValue(teamId, out var entry))
                teams[teamId] = entry = new TeamTableEntry { TeamID = teamId, TeamName = teamName.Trim() };
            return entry;
        }

        void Add(TeamTableEntry team, int goalsFor, int goalsAgainst)
        {
            team.Played++;
            team.GoalsFor += goalsFor;
            team.GoalsAgainst += goalsAgainst;
            if (goalsFor > goalsAgainst) { team.Wins++; team.Form.Add("V"); }
            else if (goalsFor < goalsAgainst) { team.Losses++; team.Form.Add("F"); }
            else { team.Draws++; team.Form.Add("O"); }
        }

        foreach (var match in matches
                     .Where(m => m.MatchStatus == PlayedStatus && m.GoalsHomeTeam.HasValue && m.GoalsAwayTeam.HasValue)
                     .OrderBy(m => m.MatchDateTime))
        {
            var home = Ensure(match.HomeTeamID, match.HomeTeam);
            var away = Ensure(match.AwayTeamID, match.AwayTeam);
            int homeGoals = match.GoalsHomeTeam!.Value;
            int awayGoals = match.GoalsAwayTeam!.Value;

            if (scope != "away") Add(home, homeGoals, awayGoals);
            if (scope != "home") Add(away, awayGoals, homeGoals);
        }

        foreach (var team in teams.Values)
            team.Form = team.Form.TakeLast(5).ToList();

        return teams.Values
            .OrderByDescending(t => t.Points)
            .ThenByDescending(t => t.GoalDiff)
            .ThenByDescending(t => t.GoalsFor)
            .ThenBy(t => t.TeamName)
            .ToList();
    }

    public async Task<List<TeamTableEntry>> GetSeriesTableAsync(int competitionId, string scope = "all")
    {
        var matches = await GetMatchesAsync(competitionId);
        return BuildTable(matches, scope);
    }

    public async Task<PositionHistory?> GetPositionHistoryAsync(int competitionId)
    {
        var matches = await GetMatchesAsync(competitionId);
        var played = matches.Where(m => m.MatchStatus == PlayedStatus && m.GoalsHomeTeam.HasValue && m.Round > 0).ToList();
        var rounds = played.Select(m => m.Round).Distinct().OrderBy(r => r).ToList();
        if (rounds.Count < 2)
            return null;

        var history = new PositionHistory { Rounds = rounds };
        var finalTable = BuildTable(played);
        foreach (var team in finalTable)
            history.Ranks[team.TeamName] = new List<int?>();

        foreach (var round in rounds)
        {
            var table = BuildTable(played.Where(m => m.Round <= round));
            foreach (var team in finalTable)
            {
                var index = table.FindIndex(t => t.TeamName == team.TeamName);
                history.Ranks[team.TeamName].Add(index >= 0 ? index + 1 : null);
            }
        }

        return history;
    }

    public async Task<string> GetCompetitionNameAsync(int competitionId)
    {
        var matches = await GetMatchesAsync(competitionId);
        return matches.FirstOrDefault()?.CompetitionName.Trim() ?? "";
    }

    public async Task<List<Season>> GetSeasonsAsync()
    {
        var json = await GetApiJsonAsync("seasons/", TimeSpan.FromHours(1));
        return JsonSerializer.Deserialize<List<Season>>(json, JsonOptions) ?? new List<Season>();
    }

    public async Task<List<Federation>> GetFederationsAsync()
    {
        var json = await GetApiJsonAsync("federations/", TimeSpan.FromHours(1));
        var federations = JsonSerializer.Deserialize<List<Federation>>(json, JsonOptions) ?? new List<Federation>();
        return federations.OrderBy(f => f.Name).ToList();
    }

    public async Task<List<Competition>> GetCompetitionsAsync(int seasonId = 43, int federationId = 8)
    {
        var json = await GetApiJsonAsync(
            $"seasons/{seasonId}/federations/{federationId}/competitions", TimeSpan.FromMinutes(30));
        var competitions = JsonSerializer.Deserialize<List<Competition>>(json, JsonOptions) ?? new List<Competition>();

        // Sortera på namn
        return competitions.OrderBy(c => c.Name).ToList();
    }

    // ---- Målvakter och lagstatistik ----

    public async Task<List<GoalieStanding>> GetGoalieStandingsAsync(int competitionId)
    {
        var data = await GetCompetitionDataAsync(competitionId);
        var goalies = new Dictionary<(int PlayerID, string Team), GoalieStanding>();

        foreach (var pm in data.Played)
        {
            foreach (var g in pm.Analysis.Goalies)
            {
                var team = g.IsHome ? pm.HomeTeam : pm.AwayTeam;
                var key = (g.PlayerID, team);
                if (!goalies.TryGetValue(key, out var standing))
                    goalies[key] = standing = new GoalieStanding { PlayerID = g.PlayerID, Name = g.Name.Trim(), Team = team };

                standing.Matches++;
                standing.Seconds += g.Seconds;
                standing.GoalsAgainst += g.GoalsAgainst;
                if (g.HasShots)
                {
                    standing.ShotsAgainst += g.ShotsAgainst;
                    standing.GoalsAgainstWithShots += g.GoalsAgainst;
                }
            }
        }

        return goalies.Values.ToList();
    }

    public async Task<List<TeamSpecialStats>> GetTeamStatsAsync(int competitionId)
    {
        var data = await GetCompetitionDataAsync(competitionId);
        var teams = new Dictionary<string, TeamSpecialStats>();

        TeamSpecialStats Ensure(string name)
        {
            if (!teams.TryGetValue(name, out var stats))
                teams[name] = stats = new TeamSpecialStats { TeamName = name };
            return stats;
        }

        foreach (var pm in data.Played)
        {
            var a = pm.Analysis;
            foreach (var isHome in new[] { true, false })
            {
                var stats = Ensure(isHome ? pm.HomeTeam : pm.AwayTeam);
                var goalsFor = (isHome ? pm.Match.GoalsHomeTeam : pm.Match.GoalsAwayTeam) ?? a.Goals.Count(g => g.IsHome == isHome);
                var goalsAgainst = (isHome ? pm.Match.GoalsAwayTeam : pm.Match.GoalsHomeTeam) ?? a.Goals.Count(g => g.IsHome != isHome);

                stats.Matches++;
                stats.GoalsFor += goalsFor;
                stats.GoalsAgainst += goalsAgainst;
                stats.PowerPlayGoals += isHome ? a.HomePpGoals : a.AwayPpGoals;
                stats.PowerPlayOpportunities += isHome ? a.HomePpOpportunities : a.AwayPpOpportunities;
                stats.PowerPlayGoalsAgainst += isHome ? a.AwayPpGoals : a.HomePpGoals;
                stats.TimesShortHanded += isHome ? a.AwayPpOpportunities : a.HomePpOpportunities;
                stats.ShortHandedGoals += a.Goals.Count(g => g.IsHome == isHome && g.Strength == GoalStrength.ShortHanded);
                stats.PenaltyMinutes += isHome ? a.HomePenaltyMinutes : a.AwayPenaltyMinutes;

                if (a.HasShots)
                {
                    stats.MatchesWithShots++;
                    stats.ShotsFor += isHome ? a.ShotsHome : a.ShotsAway;
                    stats.ShotsAgainst += isHome ? a.ShotsAway : a.ShotsHome;
                    stats.GoalsForWithShots += goalsFor;
                    stats.GoalsAgainstWithShots += goalsAgainst;
                }

                foreach (var goal in a.Goals)
                {
                    var periodIndex = Math.Clamp(goal.Period, 1, 4) - 1;
                    var forUs = goal.IsHome == isHome;
                    (forUs ? stats.GoalsForByPeriod : stats.GoalsAgainstByPeriod)[periodIndex]++;

                    if (goal.Period is >= 1 and <= 3)
                    {
                        var interval = (goal.Period - 1) * 4 + Math.Min(3, goal.Minute / 5);
                        (forUs ? stats.GoalsForByInterval : stats.GoalsAgainstByInterval)[interval]++;
                    }
                }
            }
        }

        return teams.Values.OrderBy(t => t.TeamName).ToList();
    }

    // ---- Match ----

    public async Task<(PlayedMatch? Match, List<PlayerMatchLine> Players)> GetMatchReportAsync(int matchId)
    {
        var detail = await GetMatchDetailsAsync(matchId);
        if (detail == null)
            return (null, new List<PlayerMatchLine>());

        var lineup = await GetLineupAsync(matchId);
        var pm = new PlayedMatch { Match = detail, Lineup = lineup, Analysis = MatchAnalyzer.Analyze(detail) };
        var players = BuildPlayerLines(pm)
            .OrderByDescending(p => p.Points)
            .ThenByDescending(p => p.Goals)
            .ThenBy(p => p.Name)
            .ToList();

        return (pm, players);
    }

    public async Task<MatchPreview> GetMatchPreviewAsync(int competitionId, string homeTeam, string awayTeam)
    {
        var tableTask = GetSeriesTableAsync(competitionId);
        var standingsTask = GetStandingsAsync(competitionId);
        var teamStatsTask = GetTeamStatsAsync(competitionId);
        var matchesTask = GetMatchesAsync(competitionId);
        await Task.WhenAll(tableTask, standingsTask, teamStatsTask, matchesTask);

        var table = await tableTask;
        var standings = await standingsTask;
        var teamStats = await teamStatsTask;

        List<PlayerStanding> Top(string team) => standings
            .Where(p => p.Team == team)
            .OrderByDescending(p => p.Points).ThenByDescending(p => p.Goals).ThenBy(p => p.Name)
            .Take(3)
            .ToList();

        var meetings = (await matchesTask)
            .Where(m => m.MatchStatus == PlayedStatus && m.GoalsHomeTeam.HasValue
                        && ((m.HomeTeam.Trim() == homeTeam && m.AwayTeam.Trim() == awayTeam)
                            || (m.HomeTeam.Trim() == awayTeam && m.AwayTeam.Trim() == homeTeam)))
            .OrderByDescending(m => m.MatchDateTime)
            .Select(m => ToResult(m, homeTeam))
            .ToList();

        return new MatchPreview
        {
            Home = table.FirstOrDefault(t => t.TeamName == homeTeam),
            Away = table.FirstOrDefault(t => t.TeamName == awayTeam),
            HomeRank = table.FindIndex(t => t.TeamName == homeTeam) + 1,
            AwayRank = table.FindIndex(t => t.TeamName == awayTeam) + 1,
            TeamCount = table.Count,
            HomeStats = teamStats.FirstOrDefault(t => t.TeamName == homeTeam),
            AwayStats = teamStats.FirstOrDefault(t => t.TeamName == awayTeam),
            HomeTopPlayers = Top(homeTeam),
            AwayTopPlayers = Top(awayTeam),
            PreviousMeetings = meetings
        };
    }

    // ---- Spelare ----

    public async Task<PlayerPageViewModel> GetPlayerPageAsync(int competitionId, int playerId)
    {
        var dataTask = GetCompetitionDataAsync(competitionId);
        var playerTask = GetPlayerAsync(playerId);
        var standingsTask = GetStandingsAsync(competitionId);
        var goaliesTask = GetGoalieStandingsAsync(competitionId);
        await Task.WhenAll(dataTask, playerTask, standingsTask, goaliesTask);

        var data = await dataTask;
        var player = await playerTask;
        var standings = await standingsTask;

        var lines = data.Played
            .SelectMany(BuildPlayerLines)
            .Where(l => l.PlayerID == playerId)
            .OrderBy(l => l.MatchDateTime)
            .ToList();

        var goalieLines = data.Played
            .SelectMany(pm => pm.Analysis.Goalies.Where(g => g.PlayerID == playerId).Select(g => (pm.Match.MatchID, g)))
            .ToDictionary(x => x.MatchID, x => x.g);

        var goalieTotals = (await goaliesTask).Where(g => g.PlayerID == playerId).ToList();
        GoalieStanding? goalieTotal = goalieTotals.Count == 0 ? null : new GoalieStanding
        {
            PlayerID = playerId,
            Name = goalieTotals[0].Name,
            Team = string.Join(", ", goalieTotals.Select(g => g.Team)),
            Matches = goalieTotals.Sum(g => g.Matches),
            Seconds = goalieTotals.Sum(g => g.Seconds),
            GoalsAgainst = goalieTotals.Sum(g => g.GoalsAgainst),
            ShotsAgainst = goalieTotals.Sum(g => g.ShotsAgainst),
            GoalsAgainstWithShots = goalieTotals.Sum(g => g.GoalsAgainstWithShots)
        };

        // Placering i poängligan (summerat över lag)
        var pointsByPlayer = standings
            .GroupBy(s => s.PlayerID)
            .Select(g => (PlayerID: g.Key, Points: g.Sum(s => s.Points)))
            .ToList();
        var myPoints = pointsByPlayer.FirstOrDefault(p => p.PlayerID == playerId).Points;

        return new PlayerPageViewModel
        {
            CompetitionId = competitionId,
            CompetitionName = data.CompetitionName,
            PlayerID = playerId,
            Name = player?.Name is { Length: > 0 } name ? name : lines.LastOrDefault()?.Name ?? "",
            Details = player,
            Lines = lines,
            Totals = standings.Where(s => s.PlayerID == playerId).ToList(),
            GoalieLines = goalieLines,
            GoalieTotal = goalieTotal,
            PointsRank = lines.Count > 0 ? pointsByPlayer.Count(p => p.Points > myPoints) + 1 : 0,
            RankedPlayers = pointsByPlayer.Count
        };
    }

    // ---- Lag ----

    public async Task<TeamAnalysisViewModel> GetTeamAnalysisAsync(int competitionId, string teamName)
    {
        var dataTask = GetCompetitionDataAsync(competitionId);
        var standingsTask = GetStandingsAsync(competitionId);
        var teamStatsTask = GetTeamStatsAsync(competitionId);
        await Task.WhenAll(dataTask, standingsTask, teamStatsTask);

        var data = await dataTask;
        var allStandings = await standingsTask;
        var teamStats = await teamStatsTask;
        var seriesTable = BuildTable(data.Matches);

        // Lagets matcher, senaste först
        var teamMatches = data.Matches
            .Where(m => m.HomeTeam.Trim() == teamName || m.AwayTeam.Trim() == teamName)
            .OrderByDescending(m => m.MatchDateTime)
            .ToList();

        var played = teamMatches.Where(m => m.MatchStatus == PlayedStatus && m.GoalsHomeTeam.HasValue).ToList();
        var upcoming = teamMatches.Where(m => m.MatchStatus != PlayedStatus).OrderBy(m => m.MatchDateTime).ToList();

        var tableEntry = seriesTable.FirstOrDefault(t => t.TeamName == teamName);
        var tableRank = seriesTable.FindIndex(t => t.TeamName == teamName) + 1;

        // Hemma/borta, snitt och sviter
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

        var teamPlayed = data.Played
            .Where(pm => pm.HomeTeam == teamName || pm.AwayTeam == teamName)
            .OrderByDescending(pm => pm.Match.MatchDateTime)
            .ToList();

        // Formspelare: poäng i de senaste matcherna
        const int formCount = 3;
        var seasonLookup = allStandings.Where(p => p.Team == teamName).ToDictionary(p => p.PlayerID);
        var formPlayers = teamPlayed
            .Take(formCount)
            .SelectMany(BuildPlayerLines)
            .Where(l => l.Team == teamName && l.Points > 0)
            .GroupBy(l => l.PlayerID)
            .Select(g => new FormPlayer
            {
                PlayerID = g.Key,
                Name = g.First().Name,
                FormGoals = g.Sum(l => l.Goals),
                FormAssists = g.Sum(l => l.Assists),
                SeasonStats = seasonLookup.GetValueOrDefault(g.Key)
            })
            .OrderByDescending(fp => fp.FormPoints)
            .ThenByDescending(fp => fp.FormGoals)
            .Take(8)
            .ToList();

        // Vanligaste målskytt–assist-paren
        var duos = teamPlayed
            .SelectMany(pm => pm.Analysis.Goals.Where(g =>
                g.PlayerID > 0 && g.AssistID > 0 && (g.IsHome ? pm.HomeTeam : pm.AwayTeam) == teamName))
            .GroupBy(g => (g.PlayerID, g.AssistID))
            .Select(g => new ScoringDuo
            {
                ScorerID = g.Key.PlayerID,
                Scorer = g.First().PlayerName.Trim(),
                AssistID = g.Key.AssistID,
                Assist = g.First().AssistName.Trim(),
                Goals = g.Count()
            })
            .Where(d => d.Goals > 1)
            .OrderByDescending(d => d.Goals)
            .ThenBy(d => d.Scorer)
            .Take(5)
            .ToList();

        // Inbördes möten per motståndare
        var headToHead = played
            .GroupBy(m => m.HomeTeam.Trim() == teamName ? m.AwayTeam.Trim() : m.HomeTeam.Trim())
            .Select(g =>
            {
                var results = g.Select(m => ToResult(m, teamName)).OrderBy(r => r.MatchDateTime).ToList();
                return new HeadToHeadRecord
                {
                    Opponent = g.Key,
                    Wins = results.Count(r => r.ResultLabel == "V"),
                    Draws = results.Count(r => r.ResultLabel == "O"),
                    Losses = results.Count(r => r.ResultLabel == "F"),
                    GoalsFor = results.Sum(r => r.GoalsFor ?? 0),
                    GoalsAgainst = results.Sum(r => r.GoalsAgainst ?? 0),
                    Matches = results
                };
            })
            .OrderBy(h => seriesTable.FindIndex(t => t.TeamName == h.Opponent))
            .ToList();

        var topPlayers = allStandings
            .Where(p => p.Team == teamName)
            .OrderByDescending(p => p.Points).ThenByDescending(p => p.Goals).ThenBy(p => p.Name)
            .ToList();

        var ppGoals = teamStats.Sum(t => t.PowerPlayGoals);
        var ppOpportunities = teamStats.Sum(t => t.PowerPlayOpportunities);

        int totalPlayed = homePlayed + awayPlayed;
        return new TeamAnalysisViewModel
        {
            CompetitionId = competitionId,
            CompetitionName = data.CompetitionName,
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
            FormMatchCount = formCount,
            Stats = teamStats.FirstOrDefault(t => t.TeamName == teamName),
            LeaguePowerPlayPercent = ppOpportunities > 0 ? ppGoals * 100.0 / ppOpportunities : null,
            LeaguePenaltyKillPercent = ppOpportunities > 0 ? 100 - ppGoals * 100.0 / ppOpportunities : null,
            Duos = duos,
            HeadToHead = headToHead
        };
    }

    private static TeamMatchResult ToResult(Match m, string teamName)
    {
        bool isHome = m.HomeTeam.Trim() == teamName;
        return new TeamMatchResult
        {
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
}
