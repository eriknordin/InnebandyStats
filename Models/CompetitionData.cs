using InnebandyStats.Models.Api;

namespace InnebandyStats.Models;

// En spelad match med detaljer, lineup och analys
public class PlayedMatch
{
    public Match Match { get; set; } = new();
    public Lineup? Lineup { get; set; }
    public MatchAnalysis Analysis { get; set; } = new();

    public string HomeTeam => Match.HomeTeam.Trim();
    public string AwayTeam => Match.AwayTeam.Trim();
}

// Allt underlag för en serie, hämtat en gång och delat av alla vyer
public class CompetitionData
{
    public int CompetitionId { get; set; }
    public string CompetitionName { get; set; } = "";
    public List<Match> Matches { get; set; } = new();
    public List<PlayedMatch> Played { get; set; } = new();
}

// En spelares insats i en match
public class PlayerMatchLine
{
    public int MatchID { get; set; }
    public DateTime MatchDateTime { get; set; }
    public int PlayerID { get; set; }
    public string Name { get; set; } = "";
    public int? ShirtNo { get; set; }
    public string Position { get; set; } = "";
    public bool Captain { get; set; }
    public string Team { get; set; } = "";
    public string Opponent { get; set; } = "";
    public bool IsHome { get; set; }
    // Fanns i laguppställningen (eller uppställning saknas för matchen)
    public bool Played { get; set; }
    public int GoalsFor { get; set; }
    public int GoalsAgainst { get; set; }
    public int Goals { get; set; }
    public int Assists { get; set; }
    public int Points => Goals + Assists;
    public int PenaltyMinutes { get; set; }
    public int PowerPlayGoals { get; set; }
    public int ShortHandedGoals { get; set; }
    public int GameWinningGoals { get; set; }

    public string ResultLabel => GoalsFor > GoalsAgainst ? "V" : GoalsFor == GoalsAgainst ? "O" : "F";
}

public class GoalieStanding
{
    public int PlayerID { get; set; }
    public string Name { get; set; } = "";
    public string Team { get; set; } = "";
    public int Matches { get; set; }
    public int Seconds { get; set; }
    public int GoalsAgainst { get; set; }
    // Bara matcher där skott registrerats räknas in i räddningsprocenten
    public double ShotsAgainst { get; set; }
    public int GoalsAgainstWithShots { get; set; }

    public double Minutes => Seconds / 60.0;
    public double GoalsAgainstAverage => Seconds > 0 ? GoalsAgainst * 3600.0 / Seconds : 0;
    public double? SavePercentage => ShotsAgainst > 0
        ? Math.Max(0, (ShotsAgainst - GoalsAgainstWithShots) / ShotsAgainst * 100)
        : null;
}
