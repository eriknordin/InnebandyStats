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
        var request = await CreateAuthorizedRequest(
            $"https://api.innebandy.se/v2/api/competitions/{competitionId}/matches");

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<Match>>(json, JsonOptions) ?? new List<Match>();
    }

    public async Task<Match?> GetMatchDetailsAsync(int matchId)
    {
        var request = await CreateAuthorizedRequest(
            $"https://api.innebandy.se/v2/api/matches/{matchId}");

        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<Match>(json, JsonOptions);
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
        var playerStats = new Dictionary<int, PlayerStanding>();

        foreach (var lineup in lineups)
        {
            var homeTeamName = lineup.HomeTeam.Trim();
            var awayTeamName = lineup.AwayTeam.Trim();

            foreach (var p in lineup.HomeTeamPlayers)
            {
                EnsurePlayer(playerStats, p.PlayerID, p.Name, homeTeamName);
                playerStats[p.PlayerID].Matches++;
                if (p.Age > 0) playerStats[p.PlayerID].Age = p.Age;
                if (p.BirthYear > 0) playerStats[p.PlayerID].BirthYear = p.BirthYear;
            }

            foreach (var p in lineup.AwayTeamPlayers)
            {
                EnsurePlayer(playerStats, p.PlayerID, p.Name, awayTeamName);
                playerStats[p.PlayerID].Matches++;
                if (p.Age > 0) playerStats[p.PlayerID].Age = p.Age;
                if (p.BirthYear > 0) playerStats[p.PlayerID].BirthYear = p.BirthYear;
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
                    EnsurePlayer(playerStats, evt.PlayerID, evt.PlayerName, teamName);
                    playerStats[evt.PlayerID].Goals++;

                    // Assist
                    if (evt.PlayerAssistID > 0)
                    {
                        EnsurePlayer(playerStats, evt.PlayerAssistID, evt.PlayerAssistName, teamName);
                        playerStats[evt.PlayerAssistID].Assists++;
                    }
                }

                // Utvisning (MatchEventTypeID = 2)
                if (evt.MatchEventTypeID == 2 && evt.PlayerID > 0)
                {
                    var teamName = evt.MatchTeamName?.Trim() ?? "";
                    EnsurePlayer(playerStats, evt.PlayerID, evt.PlayerName, teamName);
                    playerStats[evt.PlayerID].PenaltyMinutes += 2;
                }
            }
        }

        // 5. Hämta födelseår från player-API (lineups saknar BirthYear)
        var playerIds = playerStats.Keys.ToList();
        _logger.LogInformation("Hämtar spelardetaljer för {Count} spelare...", playerIds.Count);

        foreach (var batch in playerIds.Chunk(10))
        {
            var tasks = batch.Select(id => GetPlayerAsync(id));
            var results = await Task.WhenAll(tasks);

            foreach (var player in results.Where(p => p != null))
            {
                if (playerStats.ContainsKey(player!.PlayerID))
                {
                    if (player.Age > 0) playerStats[player.PlayerID].Age = player.Age;
                    if (player.BirthYear > 0) playerStats[player.PlayerID].BirthYear = player.BirthYear;
                    if (!string.IsNullOrEmpty(player.Name))
                        playerStats[player.PlayerID].Name = player.Name;
                }
            }
        }

        var standings = playerStats.Values.ToList();

        // Spara i cache i 10 minuter
        _cache.Set(cacheKey, standings, TimeSpan.FromMinutes(10));
        _logger.LogInformation("Poängliga cachad för tävling {CompetitionId} ({Count} spelare).", competitionId, standings.Count);

        return standings;
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
}
