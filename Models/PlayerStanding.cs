namespace InnebandyStats.Models;

public class PlayerStanding
{
    public int PlayerID { get; set; }
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public int BirthYear { get; set; }
    public string Team { get; set; } = "";
    public int Matches { get; set; }
    public int Goals { get; set; }
    public int Assists { get; set; }
    public int Points => Goals + Assists;
    public double PointsPerGame => Matches > 0 ? Math.Round((double)Points / Matches, 2) : 0;
    public int PenaltyMinutes { get; set; }
}
