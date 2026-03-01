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

    public async Task<IActionResult> Index()
    {
        var vm = await BuildIndexViewModelAsync();
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Index(int competitionId, int seasonId, int federationId)
    {
        if (competitionId <= 0)
        {
            var vm = await BuildIndexViewModelAsync(seasonId, federationId);
            vm.ErrorMessage = "Välj en serie.";
            return View(vm);
        }

        try
        {
            await _apiService.GetStandingsAsync(competitionId);
            return RedirectToAction("Standings", new { id = competitionId });
        }
        catch (Exception ex)
        {
            var vm = await BuildIndexViewModelAsync(seasonId, federationId);
            vm.CompetitionId = competitionId;
            vm.ErrorMessage = $"Ett fel uppstod: {ex.Message}";
            return View(vm);
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetCompetitions(int seasonId, int federationId)
    {
        var competitions = await _apiService.GetCompetitionsAsync(seasonId, federationId);
        return Json(competitions.Select(c => new { c.CompetitionID, c.Name }));
    }

    public async Task<IActionResult> Standings(int id, string? team, int? age, int? birthyear, string sort = "points", bool desc = true)
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

    private async Task<CompetitionViewModel> BuildIndexViewModelAsync(int? seasonId = null, int? federationId = null)
    {
        var seasons = await _apiService.GetSeasonsAsync();
        var federations = await _apiService.GetFederationsAsync();

        var currentSeason = seasons.FirstOrDefault(s => s.IsCurrentSeason);
        var selectedSeasonId = seasonId ?? currentSeason?.SeasonID ?? seasons.FirstOrDefault()?.SeasonID ?? 43;
        var selectedFederationId = federationId ?? 8; // Stockholms IBF default

        var competitions = await _apiService.GetCompetitionsAsync(selectedSeasonId, selectedFederationId);

        return new CompetitionViewModel
        {
            AvailableSeasons = seasons,
            AvailableFederations = federations,
            AvailableCompetitions = competitions,
            SelectedSeasonId = selectedSeasonId,
            SelectedFederationId = selectedFederationId
        };
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
