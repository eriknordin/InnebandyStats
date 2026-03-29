namespace InnebandyStats.Models;

public class TeamMatchResult
{
    public int MatchID { get; set; }
    public DateTime MatchDateTime { get; set; }
    public string Opponent { get; set; } = "";
    public bool IsHome { get; set; }
    public int? GoalsFor { get; set; }
    public int? GoalsAgainst { get; set; }
    public int MatchStatus { get; set; }
    public string RoundName { get; set; } = "";
    public int Round { get; set; }

    public string ResultLabel => GoalsFor.HasValue && GoalsAgainst.HasValue
        ? (GoalsFor > GoalsAgainst ? "V" : GoalsFor == GoalsAgainst ? "O" : "F")
        : "";

    public string ResultBadgeClass => ResultLabel switch
    {
        "V" => "bg-success",
        "O" => "bg-warning text-dark",
        "F" => "bg-danger",
        _ => "bg-secondary"
    };
}

public class FormPlayer
{
    public int PlayerID { get; set; }
    public string Name { get; set; } = "";
    public int FormGoals { get; set; }
    public int FormAssists { get; set; }
    public int FormPoints => FormGoals + FormAssists;
    public PlayerStanding? SeasonStats { get; set; }
}

public class TeamAnalysisViewModel
{
    public int CompetitionId { get; set; }
    public string CompetitionName { get; set; } = "";
    public string TeamName { get; set; } = "";
    public int TableRank { get; set; }
    public TeamTableEntry? TableEntry { get; set; }

    public List<TeamMatchResult> RecentMatches { get; set; } = new();
    public List<TeamMatchResult> UpcomingMatches { get; set; } = new();

    public List<PlayerStanding> TopPlayers { get; set; } = new();
    public List<FormPlayer> FormPlayers { get; set; } = new();

    public double AvgGoalsFor { get; set; }
    public double AvgGoalsAgainst { get; set; }

    public int HomePlayed { get; set; }
    public int HomeWins { get; set; }
    public int HomeDraws { get; set; }
    public int HomeLosses { get; set; }

    public int AwayPlayed { get; set; }
    public int AwayWins { get; set; }
    public int AwayDraws { get; set; }
    public int AwayLosses { get; set; }

    public int CurrentUnbeatenStreak { get; set; }
    public int CurrentWinStreak { get; set; }
    public int FormMatchCount { get; set; } = 3;

    public int TotalPlayed => HomePlayed + AwayPlayed;
    public string? ErrorMessage { get; set; }
}
