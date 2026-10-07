namespace InnebandyStats.Models;

public class LeadersViewModel
{
    public int CompetitionId { get; set; }
    public string CompetitionName { get; set; } = "";
    public List<PlayerStanding> Players { get; set; } = new();
    public List<GoalieStanding> Goalies { get; set; } = new();
    public List<TeamSpecialStats> Teams { get; set; } = new();
    // Minsta antal matcher för snittlistor
    public int MinMatches { get; set; }
    public int MinGoalieMinutes { get; set; }
    public string? ErrorMessage { get; set; }
}
