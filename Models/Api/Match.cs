using System.Text.Json.Serialization;

namespace InnebandyStats.Models.Api;

public class Match
{
    public int MatchID { get; set; }
    public string MatchNo { get; set; } = "";
    public int CompetitionID { get; set; }
    public string CompetitionName { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public int HomeTeamID { get; set; }
    public string HomeTeam { get; set; } = "";
    public string HomeTeamShortName { get; set; } = "";
    public string HomeTeamLogotypeUrl { get; set; } = "";
    public int AwayTeamID { get; set; }
    public string AwayTeam { get; set; } = "";
    public string AwayTeamShortName { get; set; } = "";
    public string AwayTeamLogotypeUrl { get; set; } = "";
    public DateTime MatchDateTime { get; set; }
    public string Venue { get; set; } = "";
    public int? GoalsHomeTeam { get; set; }
    public int? GoalsAwayTeam { get; set; }
    public int MatchStatus { get; set; }
    public int Round { get; set; }
    public string RoundName { get; set; } = "";
    public int HomeMatchTeamID { get; set; }
    public int AwayMatchTeamID { get; set; }
    public List<MatchEvent>? Events { get; set; }
    public List<PeriodShotsOnGoal>? ShotsOnGoal { get; set; }
    public int? Spectators { get; set; }
    public string? Referee1 { get; set; }
    public string? Referee2 { get; set; }
}

public class PeriodShotsOnGoal
{
    public int Period { get; set; }
    public int? ShotsHomeTeam { get; set; }
    public int? ShotsAwayTeam { get; set; }
}
