using InnebandyStats.Models.Api;

namespace InnebandyStats.Models;

public class MatchesViewModel
{
    public int CompetitionId { get; set; }
    public string CompetitionName { get; set; } = "";
    public List<Match> Matches { get; set; } = new();
    public string? FilterTeam { get; set; }
    public string SortBy { get; set; } = "round";
    public List<string> AvailableTeams { get; set; } = new();
    public string? ErrorMessage { get; set; }
}
