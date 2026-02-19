namespace InnebandyStats.Models.Api;

public class MatchEvent
{
    public int MatchEventID { get; set; }
    public int MatchID { get; set; }
    public int MatchEventTypeID { get; set; }
    public string MatchEventType { get; set; } = "";
    public int Period { get; set; }
    public string PeriodName { get; set; } = "";
    public int Minute { get; set; }
    public int Second { get; set; }
    public int PlayerID { get; set; }
    public string PlayerName { get; set; } = "";
    public int? PlayerShirtNo { get; set; }
    public int PlayerAssistID { get; set; }
    public string PlayerAssistName { get; set; } = "";
    public int? PlayerAssistShirtNo { get; set; }
    public int MatchTeamID { get; set; }
    public bool? IsHomeTeam { get; set; }
    public string MatchTeamName { get; set; } = "";
    public string? MatchTeamShortName { get; set; }
    public int GoalsHomeTeam { get; set; }
    public int GoalsAwayTeam { get; set; }
    public string PenaltyCode { get; set; } = "";
    public string PenaltyName { get; set; } = "";
    public bool IsPpGoal { get; set; }
}
