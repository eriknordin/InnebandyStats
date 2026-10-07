namespace InnebandyStats.Models.Api;

public class Federation
{
    public int FederationID { get; set; }
    public string Name { get; set; } = "";
    // Det publika API:t skickar inte Active (returnerar bara aktiva förbund)
    public bool Active { get; set; } = true;
}
