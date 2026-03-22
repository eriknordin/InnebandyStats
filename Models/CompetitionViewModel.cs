using InnebandyStats.Models.Api;

namespace InnebandyStats.Models;

public class CompetitionViewModel
{
    public int CompetitionId { get; set; }
    public string CompetitionName { get; set; } = "";
    public List<PlayerStanding> Standings { get; set; } = new();
    public string? ErrorMessage { get; set; }

    // Filter
    public string? FilterTeam { get; set; }
    public int? FilterAge { get; set; }
    public int? FilterBirthYear { get; set; }
    public string? FilterName { get; set; }

    // Sort
    public string SortBy { get; set; } = "points";
    public bool SortDesc { get; set; } = true;

    // Available filter values
    public List<string> AvailableTeams { get; set; } = new();
    public List<int> AvailableAges { get; set; } = new();
    public List<int> AvailableBirthYears { get; set; } = new();

    // Pickers
    public List<Season> AvailableSeasons { get; set; } = new();
    public List<Federation> AvailableFederations { get; set; } = new();
    public List<Competition> AvailableCompetitions { get; set; } = new();
    public int SelectedSeasonId { get; set; }
    public int SelectedFederationId { get; set; }
}
