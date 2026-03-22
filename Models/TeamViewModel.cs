namespace InnebandyStats.Models;

public class TeamViewModel
{
    public int CompetitionId { get; set; }
    public string CompetitionName { get; set; } = "";
    public string TeamName { get; set; } = "";
    public List<PlayerStanding> Players { get; set; } = new();
    public string SortBy { get; set; } = "points";
    public bool SortDesc { get; set; } = true;
    public string? ErrorMessage { get; set; }
}
