namespace InnebandyStats.Models;

// Lagstatistik som räknas fram ur matchhändelserna
public class TeamSpecialStats
{
    public string TeamName { get; set; } = "";
    public int Matches { get; set; }
    public int GoalsFor { get; set; }
    public int GoalsAgainst { get; set; }

    public int PowerPlayGoals { get; set; }
    public int PowerPlayOpportunities { get; set; }
    public int PowerPlayGoalsAgainst { get; set; }
    public int TimesShortHanded { get; set; }
    public int ShortHandedGoals { get; set; }
    public int PenaltyMinutes { get; set; }

    public int MatchesWithShots { get; set; }
    public int ShotsFor { get; set; }
    public int ShotsAgainst { get; set; }
    public int GoalsForWithShots { get; set; }
    public int GoalsAgainstWithShots { get; set; }

    // Index 0-2 = period 1-3, index 3 = förlängning
    public int[] GoalsForByPeriod { get; set; } = new int[4];
    public int[] GoalsAgainstByPeriod { get; set; } = new int[4];
    // Femminutersintervall i ordinarie tid (period 1-3, fyra intervall per period)
    public int[] GoalsForByInterval { get; set; } = new int[12];
    public int[] GoalsAgainstByInterval { get; set; } = new int[12];

    public double? PowerPlayPercent => PowerPlayOpportunities > 0 ? PowerPlayGoals * 100.0 / PowerPlayOpportunities : null;
    public double? PenaltyKillPercent => TimesShortHanded > 0 ? 100 - PowerPlayGoalsAgainst * 100.0 / TimesShortHanded : null;
    public double? ShotsForAverage => MatchesWithShots > 0 ? (double)ShotsFor / MatchesWithShots : null;
    public double? ShotsAgainstAverage => MatchesWithShots > 0 ? (double)ShotsAgainst / MatchesWithShots : null;
    public double? ShootingPercent => ShotsFor > 0 ? GoalsForWithShots * 100.0 / ShotsFor : null;
    public double? SavePercent => ShotsAgainst > 0 ? 100 - GoalsAgainstWithShots * 100.0 / ShotsAgainst : null;
    public double PenaltyMinutesAverage => Matches > 0 ? (double)PenaltyMinutes / Matches : 0;
}

public class ScoringDuo
{
    public int ScorerID { get; set; }
    public string Scorer { get; set; } = "";
    public int AssistID { get; set; }
    public string Assist { get; set; } = "";
    public int Goals { get; set; }
}

public class HeadToHeadRecord
{
    public string Opponent { get; set; } = "";
    public int Wins { get; set; }
    public int Draws { get; set; }
    public int Losses { get; set; }
    public int GoalsFor { get; set; }
    public int GoalsAgainst { get; set; }
    public List<TeamMatchResult> Matches { get; set; } = new();
}
