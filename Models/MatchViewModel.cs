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
    public bool IsPlayed { get; set; }
    public DateTime MatchDateTime { get; set; }
    public string Venue { get; set; } = "";
    public string RoundName { get; set; } = "";
    public int? Spectators { get; set; }
    public List<string> Referees { get; set; } = new();
    public MatchAnalysis Analysis { get; set; } = new();
    public List<PlayerMatchLine> HomePlayers { get; set; } = new();
    public List<PlayerMatchLine> AwayPlayers { get; set; } = new();
    public Dictionary<int, PlayerStanding> SeasonStats { get; set; } = new();
    public MatchPreview? Preview { get; set; }
    public string? ErrorMessage { get; set; }
}

// Inför match: lagen sida vid sida
public class MatchPreview
{
    public TeamTableEntry? Home { get; set; }
    public TeamTableEntry? Away { get; set; }
    public int HomeRank { get; set; }
    public int AwayRank { get; set; }
    public int TeamCount { get; set; }
    public TeamSpecialStats? HomeStats { get; set; }
    public TeamSpecialStats? AwayStats { get; set; }
    public List<PlayerStanding> HomeTopPlayers { get; set; } = new();
    public List<PlayerStanding> AwayTopPlayers { get; set; } = new();
    // Tidigare möten i serien, sett från hemmalaget
    public List<TeamMatchResult> PreviousMeetings { get; set; } = new();
}
