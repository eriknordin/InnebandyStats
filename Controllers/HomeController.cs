using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using InnebandyStats.Models;
using InnebandyStats.Services;

namespace InnebandyStats.Controllers;

public class HomeController : Controller
{
    private readonly InnebandyApiService _apiService;

    public HomeController(InnebandyApiService apiService)
    {
        _apiService = apiService;
    }

    public IActionResult Index()
    {
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> GetSeasons()
    {
        var seasons = await _apiService.GetSeasonsAsync();
        return Json(seasons.Select(s => new { s.SeasonID, s.Name, s.IsCurrentSeason }));
    }

    [HttpGet]
    public async Task<IActionResult> GetFederations()
    {
        var federations = await _apiService.GetFederationsAsync();
        return Json(federations.Where(f => f.Active).Select(f => new { f.FederationID, f.Name }));
    }

    [HttpGet]
    public async Task<IActionResult> GetCompetitions(int seasonId, int federationId)
    {
        var competitions = await _apiService.GetCompetitionsAsync(seasonId, federationId);
        return Json(competitions.Select(c => new { c.CompetitionID, c.Name }));
    }

    public async Task<IActionResult> Standings(int id, string? team, int? age, int? birthyear, string? name, string sort = "points", bool desc = true)
    {
        if (id <= 0)
            return RedirectToAction("Index");

        try
        {
            var allStandings = await _apiService.GetStandingsAsync(id);
            var competitionName = await _apiService.GetCompetitionNameAsync(id);

            var availableTeams = allStandings
                .Select(p => p.Team)
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct()
                .OrderBy(t => t)
                .ToList();

            var availableAges = allStandings
                .Select(p => p.Age)
                .Where(a => a > 0)
                .Distinct()
                .OrderBy(a => a)
                .ToList();

            var availableBirthYears = allStandings
                .Select(p => p.BirthYear)
                .Where(y => y > 0)
                .Distinct()
                .OrderBy(y => y)
                .ToList();

            var filtered = allStandings.AsEnumerable();

            if (!string.IsNullOrEmpty(team))
                filtered = filtered.Where(p => p.Team == team);

            if (age.HasValue)
                filtered = filtered.Where(p => p.Age == age.Value);

            if (birthyear.HasValue)
                filtered = filtered.Where(p => p.BirthYear == birthyear.Value);

            if (!string.IsNullOrEmpty(name))
                filtered = filtered.Where(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

            filtered = sort switch
            {
                "name" => desc ? filtered.OrderByDescending(p => p.Name) : filtered.OrderBy(p => p.Name),
                "age" => desc ? filtered.OrderByDescending(p => p.Age) : filtered.OrderBy(p => p.Age),
                "birthyear" => desc ? filtered.OrderByDescending(p => p.BirthYear) : filtered.OrderBy(p => p.BirthYear),
                "team" => desc ? filtered.OrderByDescending(p => p.Team) : filtered.OrderBy(p => p.Team),
                "matches" => desc ? filtered.OrderByDescending(p => p.Matches) : filtered.OrderBy(p => p.Matches),
                "goals" => desc
                    ? filtered.OrderByDescending(p => p.Goals).ThenByDescending(p => p.Points)
                    : filtered.OrderBy(p => p.Goals),
                "assists" => desc
                    ? filtered.OrderByDescending(p => p.Assists).ThenByDescending(p => p.Points)
                    : filtered.OrderBy(p => p.Assists),
                "ppg" => desc
                    ? filtered.OrderByDescending(p => p.PointsPerGame).ThenByDescending(p => p.Points)
                    : filtered.OrderBy(p => p.PointsPerGame),
                "penalty" => desc
                    ? filtered.OrderByDescending(p => p.PenaltyMinutes)
                    : filtered.OrderBy(p => p.PenaltyMinutes),
                _ => desc
                    ? filtered.OrderByDescending(p => p.Points).ThenByDescending(p => p.Goals).ThenBy(p => p.Name)
                    : filtered.OrderBy(p => p.Points).ThenBy(p => p.Goals).ThenBy(p => p.Name),
            };

            var viewModel = new CompetitionViewModel
            {
                CompetitionId = id,
                CompetitionName = competitionName,
                Standings = filtered.ToList(),
                FilterTeam = team,
                FilterAge = age,
                FilterBirthYear = birthyear,
                FilterName = name,
                SortBy = sort,
                SortDesc = desc,
                AvailableTeams = availableTeams,
                AvailableAges = availableAges,
                AvailableBirthYears = availableBirthYears
            };

            return View(viewModel);
        }
        catch (Exception ex)
        {
            return View(new CompetitionViewModel
            {
                CompetitionId = id,
                ErrorMessage = $"Ett fel uppstod: {ex.Message}"
            });
        }
    }

    public async Task<IActionResult> SeriesTable(int id)
    {
        if (id <= 0)
            return RedirectToAction("Index");

        try
        {
            var table = await _apiService.GetSeriesTableAsync(id);
            var competitionName = await _apiService.GetCompetitionNameAsync(id);

            return View(new SeriesTableViewModel
            {
                CompetitionId = id,
                CompetitionName = competitionName,
                Table = table
            });
        }
        catch (Exception ex)
        {
            return View(new SeriesTableViewModel
            {
                CompetitionId = id,
                ErrorMessage = $"Ett fel uppstod: {ex.Message}"
            });
        }
    }

    public async Task<IActionResult> TeamView(int competitionId, string teamName, string sort = "points", bool desc = true)
    {
        if (competitionId <= 0 || string.IsNullOrEmpty(teamName))
            return RedirectToAction("Index");

        try
        {
            var allStandings = await _apiService.GetStandingsAsync(competitionId);
            var competitionName = await _apiService.GetCompetitionNameAsync(competitionId);

            var players = allStandings
                .Where(p => p.Team == teamName)
                .AsEnumerable();

            players = sort switch
            {
                "name" => desc ? players.OrderByDescending(p => p.Name) : players.OrderBy(p => p.Name),
                "matches" => desc ? players.OrderByDescending(p => p.Matches) : players.OrderBy(p => p.Matches),
                "goals" => desc
                    ? players.OrderByDescending(p => p.Goals).ThenByDescending(p => p.Points)
                    : players.OrderBy(p => p.Goals),
                "assists" => desc
                    ? players.OrderByDescending(p => p.Assists).ThenByDescending(p => p.Points)
                    : players.OrderBy(p => p.Assists),
                "ppg" => desc
                    ? players.OrderByDescending(p => p.PointsPerGame).ThenByDescending(p => p.Points)
                    : players.OrderBy(p => p.PointsPerGame),
                "penalty" => desc
                    ? players.OrderByDescending(p => p.PenaltyMinutes)
                    : players.OrderBy(p => p.PenaltyMinutes),
                _ => desc
                    ? players.OrderByDescending(p => p.Points).ThenByDescending(p => p.Goals).ThenBy(p => p.Name)
                    : players.OrderBy(p => p.Points).ThenBy(p => p.Goals).ThenBy(p => p.Name),
            };

            return View(new TeamViewModel
            {
                CompetitionId = competitionId,
                CompetitionName = competitionName,
                TeamName = teamName,
                Players = players.ToList(),
                SortBy = sort,
                SortDesc = desc
            });
        }
        catch (Exception ex)
        {
            return View(new TeamViewModel
            {
                CompetitionId = competitionId,
                TeamName = teamName,
                ErrorMessage = $"Ett fel uppstod: {ex.Message}"
            });
        }
    }

    public async Task<IActionResult> Matches(int id)
    {
        if (id <= 0)
            return RedirectToAction("Index");

        try
        {
            var matches = await _apiService.GetMatchesAsync(id);
            var competitionName = await _apiService.GetCompetitionNameAsync(id);

            return View(new MatchesViewModel
            {
                CompetitionId = id,
                CompetitionName = competitionName,
                Matches = matches.OrderBy(m => m.MatchDateTime).ToList()
            });
        }
        catch (Exception ex)
        {
            return View(new MatchesViewModel
            {
                CompetitionId = id,
                ErrorMessage = $"Ett fel uppstod: {ex.Message}"
            });
        }
    }

    public async Task<IActionResult> MatchView(int id, int competitionId)
    {
        if (id <= 0)
            return RedirectToAction("Index");

        try
        {
            var (matchInfo, homeStandings, awayStandings) = await _apiService.GetMatchStandingsAsync(id);
            var resolvedCompetitionId = competitionId > 0 ? competitionId : matchInfo?.CompetitionID ?? 0;
            var competitionName = resolvedCompetitionId > 0
                ? await _apiService.GetCompetitionNameAsync(resolvedCompetitionId)
                : matchInfo?.CompetitionName ?? "";

            if (matchInfo == null)
            {
                return View(new MatchViewModel
                {
                    MatchID = id,
                    CompetitionId = competitionId,
                    CompetitionName = competitionName,
                    ErrorMessage = "Matchen kunde inte hittas."
                });
            }

            // Fetch season standings for player season stats
            var seasonStats = new Dictionary<int, PlayerStanding>();
            if (resolvedCompetitionId > 0)
            {
                try
                {
                    var allStandings = await _apiService.GetStandingsAsync(resolvedCompetitionId);
                    seasonStats = allStandings.ToDictionary(p => p.PlayerID, p => p);
                }
                catch
                {
                    // Season stats are optional - continue without them
                }
            }

            return View(new MatchViewModel
            {
                MatchID = id,
                CompetitionId = resolvedCompetitionId,
                CompetitionName = competitionName,
                HomeTeam = matchInfo.HomeTeam,
                AwayTeam = matchInfo.AwayTeam,
                GoalsHomeTeam = matchInfo.GoalsHomeTeam,
                GoalsAwayTeam = matchInfo.GoalsAwayTeam,
                MatchDateTime = matchInfo.MatchDateTime,
                Venue = matchInfo.Venue,
                RoundName = matchInfo.RoundName,
                HomeTeamStandings = homeStandings,
                AwayTeamStandings = awayStandings,
                SeasonStats = seasonStats
            });
        }
        catch (Exception ex)
        {
            return View(new MatchViewModel
            {
                MatchID = id,
                CompetitionId = competitionId,
                ErrorMessage = $"Ett fel uppstod: {ex.Message}"
            });
        }
    }

    public async Task<IActionResult> TeamAnalysis(int competitionId, string teamName)
    {
        if (competitionId <= 0 || string.IsNullOrEmpty(teamName))
            return RedirectToAction("Index");

        try
        {
            var analysis = await _apiService.GetTeamAnalysisAsync(competitionId, teamName);
            return View(analysis);
        }
        catch (Exception ex)
        {
            return View(new TeamAnalysisViewModel
            {
                CompetitionId = competitionId,
                TeamName = teamName,
                ErrorMessage = $"Ett fel uppstod: {ex.Message}"
            });
        }
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
