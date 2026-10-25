namespace InnebandyStats.Models;

// Vinster, oavgjorda och förluster i ett visst matchläge
public class ResultRecord
{
    public int Wins { get; set; }
    public int Draws { get; set; }
    public int Losses { get; set; }
    public int Total => Wins + Draws + Losses;
    public int Points => Wins * 3 + Draws;
    public double? WinPercent => Total > 0 ? Wins * 100.0 / Total : null;
    public double? PointsPercent => Total > 0 ? Points * 100.0 / (Total * 3) : null;

    public void Add(int goalsFor, int goalsAgainst)
    {
        if (goalsFor > goalsAgainst) Wins++;
        else if (goalsFor == goalsAgainst) Draws++;
        else Losses++;
    }

    public override string ToString() => $"{Wins}-{Draws}-{Losses}";
}

// En match där laget vände eller tappade
public class GameSwing
{
    public TeamMatchResult Match { get; set; } = new();
    // Största underläge (upphämtning) eller ledning (tappad) under matchen
    public int Margin { get; set; }
}

// Hur laget presterar beroende på ställningen
public class GameStateStats
{
    // Matcher där målhändelserna stämmer med slutresultatet
    public int MatchesAnalyzed { get; set; }
    public ResultRecord ScoredFirst { get; set; } = new();
    public ResultRecord ConcededFirst { get; set; } = new();
    public ResultRecord LeadAfterP1 { get; set; } = new();
    public ResultRecord TiedAfterP1 { get; set; } = new();
    public ResultRecord TrailAfterP1 { get; set; } = new();
    public ResultRecord LeadAfterP2 { get; set; } = new();
    public ResultRecord TiedAfterP2 { get; set; } = new();
    public ResultRecord TrailAfterP2 { get; set; } = new();
    public ResultRecord OneGoalGames { get; set; } = new();
    // Avgjorda med fyra mål eller mer
    public ResultRecord Blowouts { get; set; } = new();
    public List<GameSwing> Comebacks { get; set; } = new();
    public List<GameSwing> BlownLeads { get; set; } = new();
    // Insläppta mål som laget svarade på inom ResponseSeconds
    public int GoalsConceded { get; set; }
    public int QuickResponses { get; set; }
    public const int ResponseSeconds = 120;

    public double? ScoredFirstPercent => MatchesAnalyzed > 0 && ScoredFirst.Total + ConcededFirst.Total > 0
        ? ScoredFirst.Total * 100.0 / (ScoredFirst.Total + ConcededFirst.Total)
        : null;
    // Andel ledningar efter två perioder som blev vinst
    public double? HoldLeadPercent => LeadAfterP2.WinPercent;
    public double? QuickResponsePercent => GoalsConceded > 0 ? QuickResponses * 100.0 / GoalsConceded : null;
}

// Mål per match och rullande snitt
public class TrendPoint
{
    public int MatchID { get; set; }
    public DateTime MatchDateTime { get; set; }
    public string Opponent { get; set; } = "";
    public int GoalsFor { get; set; }
    public int GoalsAgainst { get; set; }
    public double RollingFor { get; set; }
    public double RollingAgainst { get; set; }
}

public class DepthStats
{
    public int TeamGoals { get; set; }
    public int Scorers { get; set; }
    public int PointGetters { get; set; }
    public double? TopThreeGoalShare { get; set; }
    public List<(int PlayerID, string Name, int Goals)> TopScorers { get; set; } = new();
}

public class DisciplineStats
{
    public int Matches { get; set; }
    public int Penalties { get; set; }
    public int PenaltyMinutes { get; set; }
    public double PenaltiesPerMatch => Matches > 0 ? (double)Penalties / Matches : 0;
    public double? LeaguePenaltiesPerMatch { get; set; }
    // Index 0-2 = period 1-3, index 3 = förlängning
    public int[] ByPeriod { get; set; } = new int[4];
    public List<(string Name, int Count)> CommonTypes { get; set; } = new();
    public List<(int PlayerID, string Name, int Minutes)> TopOffenders { get; set; } = new();
}

public class SquadStats
{
    public int MatchesWithLineup { get; set; }
    public int PlayersUsed { get; set; }
    public double? AverageAge { get; set; }
    public (int PlayerID, string Name, int Age)? Youngest { get; set; }
    public (int PlayerID, string Name, int Age)? Oldest { get; set; }
    // Spelare som gjort sin första match för laget i någon av de senaste matcherna
    public List<(int PlayerID, string Name, DateTime FirstMatch)> Newcomers { get; set; } = new();
    public int NewcomerWindow { get; set; }
}

// Prognos för ett lag, från säsongssimuleringen
public class TeamProjection
{
    public string TeamName { get; set; } = "";
    public int Rank { get; set; }
    public int Played { get; set; }
    public int Points { get; set; }
    public int GoalDiff { get; set; }
    // Förväntade poäng i spelade matcher utifrån lagens målskapande
    public double ExpectedPoints { get; set; }
    public double PointsAboveExpected => Points - ExpectedPoints;
    public double ProjectedPoints { get; set; }
    public double AverageFinish { get; set; }
    // Sannolikhet (0-1) per slutplacering, index 0 = etta
    public double[] PositionOdds { get; set; } = Array.Empty<double>();
    public int RemainingMatches { get; set; }
    // Snittplacering för återstående motståndare
    public double? RemainingOpponentRank { get; set; }
    public double Attack { get; set; }
    public double Defense { get; set; }
}

public class SeasonProjection
{
    public int CompetitionId { get; set; }
    public int Simulations { get; set; }
    public int RemainingMatches { get; set; }
    public double HomeGoalsAverage { get; set; }
    public double AwayGoalsAverage { get; set; }
    public List<TeamProjection> Teams { get; set; } = new();

    public TeamProjection? For(string teamName) => Teams.FirstOrDefault(t => t.TeamName == teamName);
}

// Sannolikheter för en enskild match
public class MatchOdds
{
    public double HomeWin { get; set; }
    public double Draw { get; set; }
    public double AwayWin { get; set; }
    public double ExpectedHomeGoals { get; set; }
    public double ExpectedAwayGoals { get; set; }
}

// Allt extra underlag för lagsidan
public class TeamInsights
{
    public GameStateStats GameStates { get; set; } = new();
    public List<TrendPoint> Trend { get; set; } = new();
    public DepthStats Depth { get; set; } = new();
    public DisciplineStats Discipline { get; set; } = new();
    public SquadStats Squad { get; set; } = new();
    // Facit mot topp-, mitten- och bottenlag i nuvarande tabell
    public ResultRecord VsTop { get; set; } = new();
    public ResultRecord VsMiddle { get; set; } = new();
    public ResultRecord VsBottom { get; set; } = new();
}

// En rad i seriens lagstatistik
public class LeagueTeamRow
{
    public string TeamName { get; set; } = "";
    public int Rank { get; set; }
    public TeamTableEntry? Table { get; set; }
    public TeamSpecialStats? Stats { get; set; }
    public GameStateStats GameStates { get; set; } = new();
    public TeamProjection? Projection { get; set; }

    public double? GoalsForAverage => Stats is { Matches: > 0 } s ? (double)s.GoalsFor / s.Matches : null;
    public double? GoalsAgainstAverage => Stats is { Matches: > 0 } s ? (double)s.GoalsAgainst / s.Matches : null;
    // Skotteffektivitet + räddningsprocent; runt 100 är normalt, högt tyder på flyt
    public double? Pdo => Stats?.ShootingPercent is double sh && Stats.SavePercent is double sv ? sh + sv : null;
}

public class LeagueTeamStatsViewModel
{
    public int CompetitionId { get; set; }
    public string CompetitionName { get; set; } = "";
    public List<LeagueTeamRow> Teams { get; set; } = new();
    public bool HasShots => Teams.Any(t => t.Stats?.MatchesWithShots > 0);
    public string? ErrorMessage { get; set; }
}

public class ComparisonSide
{
    public string TeamName { get; set; } = "";
    public int Rank { get; set; }
    public TeamTableEntry? Table { get; set; }
    public TeamSpecialStats? Stats { get; set; }
    public GameStateStats GameStates { get; set; } = new();
    public TeamProjection? Projection { get; set; }
    public List<PlayerStanding> TopPlayers { get; set; } = new();
}

public class TeamComparisonViewModel
{
    public int CompetitionId { get; set; }
    public string CompetitionName { get; set; } = "";
    public List<string> AvailableTeams { get; set; } = new();
    public ComparisonSide? TeamA { get; set; }
    public ComparisonSide? TeamB { get; set; }
    // Lag A som hemmalag
    public MatchOdds? Odds { get; set; }
    // Inbördes möten sett från lag A
    public List<TeamMatchResult> Meetings { get; set; } = new();
    public int TeamCount { get; set; }
    public string? ErrorMessage { get; set; }
}
