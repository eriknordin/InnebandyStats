namespace InnebandyStats.Models.Api;

public class Competition
{
    public int CompetitionID { get; set; }
    public string Name { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public string CompetitionStatus { get; set; } = "";
    public int AgeCategoryID { get; set; }
    public string FederationName { get; set; } = "";
    public string SeasonName { get; set; } = "";
    public int GenderID { get; set; }
}
