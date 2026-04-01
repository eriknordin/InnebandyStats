namespace InnebandyStats.Models;

public class TeamSearchResult
{
    public string TeamName { get; set; } = "";
    public int TeamID { get; set; }
    public int CompetitionID { get; set; }
    public string CompetitionName { get; set; } = "";
}
