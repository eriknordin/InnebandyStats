namespace InnebandyStats.Models;

public class SeriesTableViewModel
{
    public int CompetitionId { get; set; }
    public string CompetitionName { get; set; } = "";
    public List<TeamTableEntry> Table { get; set; } = new();
    public string? ErrorMessage { get; set; }
}
