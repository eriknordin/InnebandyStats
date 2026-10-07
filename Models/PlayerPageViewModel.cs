using InnebandyStats.Models.Api;

namespace InnebandyStats.Models;

public class PlayerPageViewModel
{
    public int CompetitionId { get; set; }
    public string CompetitionName { get; set; } = "";
    public int PlayerID { get; set; }
    public string Name { get; set; } = "";
    public Player? Details { get; set; }
    public List<PlayerMatchLine> Lines { get; set; } = new();
    public List<PlayerStanding> Totals { get; set; } = new();
    public Dictionary<int, GoalieMatchStats> GoalieLines { get; set; } = new();
    public GoalieStanding? GoalieTotal { get; set; }
    public int PointsRank { get; set; }
    public int RankedPlayers { get; set; }
    public string? ErrorMessage { get; set; }

    public int Matches => Lines.Count(l => l.Played);
    public int Goals => Lines.Sum(l => l.Goals);
    public int Assists => Lines.Sum(l => l.Assists);
    public int Points => Goals + Assists;
    public int PenaltyMinutes => Lines.Sum(l => l.PenaltyMinutes);
    public string Position => Lines.Select(l => l.Position).Where(p => p != "")
        .GroupBy(p => p).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
        ?? Details?.Position ?? "";
}
