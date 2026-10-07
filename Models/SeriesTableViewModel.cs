namespace InnebandyStats.Models;

public class SeriesTableViewModel
{
    public int CompetitionId { get; set; }
    public string CompetitionName { get; set; } = "";
    // all, home eller away
    public string Scope { get; set; } = "all";
    public List<TeamTableEntry> Table { get; set; } = new();
    public PositionHistory? History { get; set; }
    public string? ErrorMessage { get; set; }
}

// Tabellplacering efter varje omgång
public class PositionHistory
{
    public List<int> Rounds { get; set; } = new();
    public Dictionary<string, List<int?>> Ranks { get; set; } = new();
    public int TeamCount => Ranks.Count;
}
