namespace InnebandyStats.Models;

// Översikt över vad Chrome-pluginet har synkat till datalagret, visas på startsidan
public class SyncOverviewViewModel
{
    public List<SyncedCompetition> Competitions { get; set; } = new();
    public bool HasSeasons { get; set; }
    public bool HasFederations { get; set; }
    public int DocumentCount { get; set; }
    public DateTime? LastReceivedAt { get; set; }
}

public class SyncedCompetition
{
    public int CompetitionId { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    // T.ex. "2026/27", härlett från matchdatumen
    public string Season { get; set; } = "";
    public int Matches { get; set; }
    public int Played { get; set; }
    // Spelade matcher vars detaljer (händelser) är synkade
    public int DetailsCollected { get; set; }
    public int LineupsCollected { get; set; }
    public List<string> Teams { get; set; } = new();
    // Svensk lokal tid
    public DateTime LastUpdatedAt { get; set; }

    public bool IsComplete => DetailsCollected >= Played;
}
