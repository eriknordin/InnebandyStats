namespace InnebandyStats.Models;

public enum TimelineEventKind
{
    Goal,
    PenaltyShotGoal,
    EmptyNetGoal,
    MissedPenaltyShot,
    Penalty,
    Timeout
}

public enum GoalStrength
{
    Even,
    PowerPlay,
    ShortHanded
}

public class TimelineEvent
{
    public TimelineEventKind Kind { get; set; }
    public int Period { get; set; }
    public string PeriodName { get; set; } = "";
    public int Minute { get; set; }
    public int Second { get; set; }
    // Sekunder från matchstart
    public int TimeSeconds { get; set; }
    public bool IsHome { get; set; }
    public int PlayerID { get; set; }
    public string PlayerName { get; set; } = "";
    public int? PlayerShirtNo { get; set; }
    public int AssistID { get; set; }
    public string AssistName { get; set; } = "";
    public int ScoreHome { get; set; }
    public int ScoreAway { get; set; }
    public string Description { get; set; } = "";
    public int PenaltyMinutes { get; set; }
    public GoalStrength Strength { get; set; }
    public bool IsGameWinner { get; set; }

    public bool IsGoal => Kind is TimelineEventKind.Goal or TimelineEventKind.PenaltyShotGoal or TimelineEventKind.EmptyNetGoal;
    public string Clock => $"{Minute:00}:{Second:00}";
}

public class GoalieMatchStats
{
    public int PlayerID { get; set; }
    public string Name { get; set; } = "";
    public bool IsHome { get; set; }
    public int Seconds { get; set; }
    public int GoalsAgainst { get; set; }
    // Skott fördelas per period efter speltid, därför decimaltal
    public double ShotsAgainst { get; set; }
    public bool HasShots { get; set; }

    public double? SavePercentage => HasShots && ShotsAgainst > 0
        ? Math.Max(0, (ShotsAgainst - GoalsAgainst) / ShotsAgainst * 100)
        : null;
}

public class PeriodShots
{
    public int Period { get; set; }
    public int Home { get; set; }
    public int Away { get; set; }
}

public class MatchAnalysis
{
    public List<TimelineEvent> Events { get; set; } = new();
    public List<GoalieMatchStats> Goalies { get; set; } = new();
    public List<PeriodShots> Shots { get; set; } = new();
    public List<int> PeriodLengths { get; set; } = new();
    public int TotalSeconds { get; set; }

    public int HomePpOpportunities { get; set; }
    public int AwayPpOpportunities { get; set; }

    public bool HasShots => Shots.Count > 0;
    public int ShotsHome => Shots.Sum(s => s.Home);
    public int ShotsAway => Shots.Sum(s => s.Away);
    public IEnumerable<TimelineEvent> Goals => Events.Where(e => e.IsGoal);
    public int HomePpGoals => Goals.Count(g => g.IsHome && g.Strength == GoalStrength.PowerPlay);
    public int AwayPpGoals => Goals.Count(g => !g.IsHome && g.Strength == GoalStrength.PowerPlay);
    public int HomePenaltyMinutes => Events.Where(e => e.Kind == TimelineEventKind.Penalty && e.IsHome).Sum(e => e.PenaltyMinutes);
    public int AwayPenaltyMinutes => Events.Where(e => e.Kind == TimelineEventKind.Penalty && !e.IsHome).Sum(e => e.PenaltyMinutes);
}
