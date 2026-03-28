using InnebandyStats.Models.Api;

namespace InnebandyStats.Models;

public class MatchesViewModel
{
    public int CompetitionId { get; set; }
    public string CompetitionName { get; set; } = "";
    public List<Match> Matches { get; set; } = new();
    public string? ErrorMessage { get; set; }
}
