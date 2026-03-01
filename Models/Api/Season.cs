namespace InnebandyStats.Models.Api;

public class Season
{
    public int SeasonID { get; set; }
    public string Name { get; set; } = "";
    public bool IsCurrentSeason { get; set; }
}
