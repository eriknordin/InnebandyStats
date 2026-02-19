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
        var competitions = await _apiService.GetCompetitionsAsync();
        return View(new CompetitionViewModel { AvailableCompetitions = competitions });
    }

    [HttpPost]
    public async Task<IActionResult> Index(int competitionId)
    {
        if (competitionId <= 0)
        {
            var competitions = await _apiService.GetCompetitionsAsync();
            return View(new CompetitionViewModel
            {
                AvailableCompetitions = competitions,
                ErrorMessage = "Välj en serie."
            });
        }

        try
        {
            await _apiService.GetStandingsAsync(competitionId);
            return RedirectToAction("Standings", new { id = competitionId });
        }
        catch (Exception ex)
        {
            var competitions = await _apiService.GetCompetitionsAsync();
            return View(new CompetitionViewModel
            {
                CompetitionId = competitionId,
                AvailableCompetitions = competitions,
                ErrorMessage = $"Ett fel uppstod: {ex.Message}"
            });
        }
    }

    public async Task<IActionResult> Standings(int id, string? team, int? age, int? birthyear, string sort = "points", bool desc = true)
    {
        if (id <= 0)
            return RedirectToAction("Index");

        try
        {
            var allStandings = await _apiService.GetStandingsAsync(id);
            var competitionName = await _apiService.GetCompetitionNameAsync(id);

            // Available filter values (from full dataset)
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

            // Apply filters
            var filtered = allStandings.AsEnumerable();

            if (!string.IsNullOrEmpty(team))
                filtered = filtered.Where(p => p.Team == team);

            if (age.HasValue)
                filtered = filtered.Where(p => p.Age == age.Value);

            if (birthyear.HasValue)
                filtered = filtered.Where(p => p.BirthYear == birthyear.Value);

            // Apply sort
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

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
