namespace InnebandyStats.Models;

public class MatchViewModel
{
    public int MatchID { get; set; }
    public int CompetitionId { get; set; }
    public string CompetitionName { get; set; } = "";
    public string HomeTeam { get; set; } = "";
    public string AwayTeam { get; set; } = "";
    public int? GoalsHomeTeam { get; set; }
    public int? GoalsAwayTeam { get; set; }
    public DateTime MatchDateTime { get; set; }
    public string Venue { get; set; } = "";
    public string RoundName { get; set; } = "";
    public List<PlayerStanding> HomeTeamStandings { get; set; } = new();
    public List<PlayerStanding> AwayTeamStandings { get; set; } = new();
    public Dictionary<int, PlayerStanding> SeasonStats { get; set; } = new();
    public string? ErrorMessage { get; set; }
}
