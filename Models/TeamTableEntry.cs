namespace InnebandyStats.Models;

public class TeamTableEntry
{
    public int TeamID { get; set; }
    public string TeamName { get; set; } = "";
    public int Played { get; set; }
    public int Wins { get; set; }
    public int Draws { get; set; }
    public int Losses { get; set; }
    public int GoalsFor { get; set; }
    public int GoalsAgainst { get; set; }
    public int GoalDiff => GoalsFor - GoalsAgainst;
    public int Points => Wins * 3 + Draws;
}
