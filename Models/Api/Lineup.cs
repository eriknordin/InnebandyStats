namespace InnebandyStats.Models.Api;

public class Lineup
{
    public int MatchID { get; set; }
    public int HomeTeamID { get; set; }
    public string HomeTeam { get; set; } = "";
    public string HomeTeamShortName { get; set; } = "";
    public int AwayTeamID { get; set; }
    public string AwayTeam { get; set; } = "";
    public string AwayTeamShortName { get; set; } = "";
    public List<LineupPlayer> HomeTeamPlayers { get; set; } = new();
    public List<LineupPlayer> AwayTeamPlayers { get; set; } = new();
}

public class LineupPlayer
{
    public int PlayerID { get; set; }
    public int TeamID { get; set; }
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public int BirthYear { get; set; }
    public int? ShirtNo { get; set; }
}
