namespace InnebandyStats.Models.Api;

public class Player
{
    public int PlayerID { get; set; }
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public int BirthYear { get; set; }
    public int? ShirtNo { get; set; }
    public string Position { get; set; } = "";
    public int Matches { get; set; }
    public int Goals { get; set; }
    public int Assists { get; set; }
    public int Points { get; set; }
    public int PenaltyMinutes { get; set; }
    public string LicensedAssociationName { get; set; } = "";
}
