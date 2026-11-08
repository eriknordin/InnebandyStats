using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using InnebandyStats.Models;

namespace InnebandyStats.Services;

// Lagrar API-dokument (rå JSON) som Chrome-pluginet har fångat på stats.innebandy.se.
// Nyckeln är samma sökväg som i API:t, t.ex. "matches/123", så resten av appen läser
// datan precis som när den kom direkt från API:t.
public sealed partial class ApiDocumentStore
{
    private readonly string _connectionString;
    private readonly ILogger<ApiDocumentStore> _logger;

    public ApiDocumentStore(IConfiguration config, IWebHostEnvironment env, ILogger<ApiDocumentStore> logger)
    {
        _logger = logger;
        var path = config["Storage:Path"];
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(env.ContentRootPath, "data", "innebandystats.db");
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Vanlig journal (inte WAL): WAL kräver delat minne och fungerar inte pålitligt på
        // nätverksdiskar som Azure App Service /home. Väntar en stund vid låst databas.
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 30 }.ToString();
        DatabasePath = path;
        Initialize();
    }

    public string DatabasePath { get; }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = DELETE;
            CREATE TABLE IF NOT EXISTS ApiDocuments (
                Path TEXT PRIMARY KEY,
                Kind TEXT NOT NULL,
                CompetitionId INTEGER,
                MatchId INTEGER,
                MatchDate TEXT,
                Json TEXT NOT NULL,
                ReceivedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ApiDocuments_Kind ON ApiDocuments (Kind);
            CREATE INDEX IF NOT EXISTS IX_ApiDocuments_Competition ON ApiDocuments (CompetitionId);
            CREATE INDEX IF NOT EXISTS IX_ApiDocuments_Match ON ApiDocuments (MatchId);
            CREATE INDEX IF NOT EXISTS IX_ApiDocuments_MatchDate ON ApiDocuments (MatchDate);
            """;
        command.ExecuteNonQuery();
        _logger.LogInformation("Datalager: {Path}", DatabasePath);
        CleanStoredDocuments(connection);
    }

    // Kontaktuppgifter (ledares e-post, telefon, personnummer) följer med i vissa API-svar,
    // t.ex. lag. Appen behöver dem inte, så de sparas aldrig.
    private static readonly HashSet<string> SensitiveProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "ContactPerson", "TeamPersons", "Email", "MobilePhone", "HomePhone", "WorkPhone", "PersonalIdentityNumber"
    };

    public static string Sanitize(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json);
        return node != null && RemoveSensitive(node) ? node.ToJsonString() : json;
    }

    private static bool RemoveSensitive(System.Text.Json.Nodes.JsonNode node)
    {
        var changed = false;
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).Where(SensitiveProperties.Contains).ToList())
            {
                obj.Remove(key);
                changed = true;
            }
            foreach (var (_, child) in obj)
                if (child != null) changed |= RemoveSensitive(child);
        }
        else if (node is System.Text.Json.Nodes.JsonArray array)
        {
            foreach (var child in array)
                if (child != null) changed |= RemoveSensitive(child);
        }
        return changed;
    }

    // Rensar kontaktuppgifter ur dokument som sparades innan rensningen fanns, och märker lag
    private void CleanStoredDocuments(SqliteConnection connection)
    {
        var dirty = new List<(string Path, string Json)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT Path, Json FROM ApiDocuments
                WHERE Json LIKE '%"ContactPerson"%' OR Json LIKE '%"TeamPersons"%' OR Json LIKE '%"Email"%'
                   OR Json LIKE '%Phone"%' OR Json LIKE '%"PersonalIdentityNumber"%'
                """;
            using var reader = select.ExecuteReader();
            while (reader.Read()) dirty.Add((reader.GetString(0), reader.GetString(1)));
        }

        using var transaction = connection.BeginTransaction();
        foreach (var (path, json) in dirty)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE ApiDocuments SET Json = $json WHERE Path = $path";
            update.Parameters.AddWithValue("$json", Sanitize(json));
            update.Parameters.AddWithValue("$path", path);
            update.ExecuteNonQuery();
        }
        using (var kinds = connection.CreateCommand())
        {
            kinds.Transaction = transaction;
            kinds.CommandText = "UPDATE ApiDocuments SET Kind = 'team' WHERE Kind = 'other' AND Path GLOB 'seasons/*/teams/*'";
            kinds.ExecuteNonQuery();
        }
        transaction.Commit();
        if (dirty.Count > 0)
            _logger.LogInformation("Tog bort kontaktuppgifter ur {Count} sparade dokument", dirty.Count);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    // Sökvägar i API:t: små bokstäver, siffror, snedstreck och bindestreck
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9/_\-]{0,199}$")]
    private static partial Regex ValidPath();

    public static string? NormalizePath(string? path)
    {
        var normalized = path?.Trim().Trim('/');
        return normalized != null && ValidPath().IsMatch(normalized) ? normalized : null;
    }

    public async Task<string?> GetAsync(string path)
    {
        var normalized = NormalizePath(path);
        if (normalized == null) return null;

        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Json FROM ApiDocuments WHERE Path = $path";
        command.Parameters.AddWithValue("$path", normalized);
        return await command.ExecuteScalarAsync() as string;
    }

    public record Document(string Path, string Json);

    // Tar bort seriens matchlista, dess matcher och laguppställningar. Returnerar antal borttagna dokument.
    public async Task<int> DeleteCompetitionAsync(int competitionId)
    {
        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TEMP TABLE IF NOT EXISTS ClearMatchIds (MatchId INTEGER PRIMARY KEY);
            DELETE FROM ClearMatchIds;
            INSERT OR IGNORE INTO ClearMatchIds
                SELECT json_extract(m.value, '$.MatchID') FROM ApiDocuments d, json_each(d.Json) m
                WHERE d.Path = $listPath AND json_extract(m.value, '$.MatchID') IS NOT NULL;
            INSERT OR IGNORE INTO ClearMatchIds
                SELECT MatchId FROM ApiDocuments WHERE Kind = 'match' AND CompetitionId = $competitionId;
            DELETE FROM ApiDocuments
            WHERE Path = $listPath
               OR (Kind IN ('match', 'lineup') AND MatchId IN (SELECT MatchId FROM ClearMatchIds));
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$listPath", $"competitions/{competitionId}/matches");
        command.Parameters.AddWithValue("$competitionId", competitionId);
        var deleted = Convert.ToInt32(await command.ExecuteScalarAsync());
        await transaction.CommitAsync();
        return deleted;
    }

    // Ålder och födelseår per spelare ur synkade lagtrupper (seasons/{s}/teams/{t})
    public async Task<Dictionary<int, (int Age, int BirthYear)>> GetRosterAgesAsync()
    {
        var ages = new Dictionary<int, (int Age, int BirthYear)>();
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT json_extract(p.value, '$.PlayerID'), coalesce(json_extract(p.value, '$.Age'), 0),
                   coalesce(json_extract(p.value, '$.BirthYear'), 0)
            FROM ApiDocuments d, json_each(d.Json, '$.Players') p
            WHERE d.Kind = 'team'
            ORDER BY d.ReceivedAt
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.IsDBNull(0)) continue;
            var birthYear = reader.GetInt32(2);
            if (birthYear > 0) ages[reader.GetInt32(0)] = (reader.GetInt32(1), birthYear);
        }
        return ages;
    }

    // När ett dokument senast togs emot (UTC)
    public async Task<DateTime?> GetReceivedAtAsync(string path)
    {
        var normalized = NormalizePath(path);
        if (normalized == null) return null;
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ReceivedAt FROM ApiDocuments WHERE Path = $path";
        command.Parameters.AddWithValue("$path", normalized);
        return await command.ExecuteScalarAsync() is string value ? ParseUtc(value) : null;
    }

    // Vilken synkad serie (matchlista) en match ingår i
    public async Task<int?> FindCompetitionForMatchAsync(int matchId)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.CompetitionId FROM ApiDocuments d, json_each(d.Json) m
            WHERE d.Kind = 'matchlist' AND json_extract(m.value, '$.MatchID') = $matchId
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$matchId", matchId);
        return await command.ExecuteScalarAsync() is long id ? (int)id : null;
    }

    // Vilka serier som är synkade och hur komplett varje serie är
    public async Task<SyncOverviewViewModel> GetOverviewAsync()
    {
        var overview = new SyncOverviewViewModel();
        await using var connection = Open();

        // Matchdetaljer: status och mottagningstid per match
        var details = new Dictionary<int, (int Status, DateTime ReceivedAt)>();
        var lineups = new HashSet<int>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Kind, MatchId, coalesce(json_extract(Json, '$.MatchStatus'), 0), ReceivedAt
                FROM ApiDocuments WHERE Kind IN ('match', 'lineup') AND MatchId IS NOT NULL
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader.GetString(0) == "lineup")
                    lineups.Add(reader.GetInt32(1));
                else
                    details[reader.GetInt32(1)] = (reader.GetInt32(2), ParseUtc(reader.GetString(3)));
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT count(*), max(ReceivedAt),
                       sum(CASE WHEN Path = 'seasons' THEN 1 ELSE 0 END),
                       sum(CASE WHEN Path = 'federations' THEN 1 ELSE 0 END)
                FROM ApiDocuments
                """;
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                overview.DocumentCount = reader.GetInt32(0);
                overview.LastReceivedAt = reader.IsDBNull(1) ? null : MatchState.ToSwedish(ParseUtc(reader.GetString(1)));
                overview.HasSeasons = !reader.IsDBNull(2) && reader.GetInt32(2) > 0;
                overview.HasFederations = !reader.IsDBNull(3) && reader.GetInt32(3) > 0;
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT CompetitionId, Json, ReceivedAt FROM ApiDocuments WHERE Kind = 'matchlist'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var competition = DescribeCompetition(reader.GetInt32(0), reader.GetString(1), ParseUtc(reader.GetString(2)), details, lineups);
                if (competition != null) overview.Competitions.Add(competition);
            }
        }

        overview.Competitions = overview.Competitions
            .OrderByDescending(c => c.Season)
            .ThenBy(c => c.Name, StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), true))
            .ToList();
        return overview;
    }

    private static SyncedCompetition? DescribeCompetition(int competitionId, string json, DateTime listReceivedAt,
        Dictionary<int, (int Status, DateTime ReceivedAt)> details, HashSet<int> lineups)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        var result = new SyncedCompetition { CompetitionId = competitionId };
        var teams = new SortedSet<string>(StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), true));
        var lastUpdated = listReceivedAt;
        DateTime? firstMatch = null;

        foreach (var m in doc.RootElement.EnumerateArray())
        {
            result.Matches++;
            if (result.Name.Length == 0 && m.TryGetProperty("CompetitionName", out var name)) result.Name = name.GetString()?.Trim() ?? "";
            if (result.Category.Length == 0 && m.TryGetProperty("CategoryName", out var category)) result.Category = category.GetString()?.Trim() ?? "";
            foreach (var teamProperty in new[] { "HomeTeam", "AwayTeam" })
                if (m.TryGetProperty(teamProperty, out var team) && team.GetString() is { Length: > 0 } t) teams.Add(t.Trim());
            if (m.TryGetProperty("MatchDateTime", out var dt) && dt.TryGetDateTime(out var date) && (firstMatch == null || date < firstMatch))
                firstMatch = date;

            var matchId = m.TryGetProperty("MatchID", out var id) && id.TryGetInt32(out var mid) ? mid : 0;
            var played = m.TryGetProperty("MatchStatus", out var status) && status.TryGetInt32(out var s) && s == MatchState.PlayedStatus
                         && m.TryGetProperty("GoalsHomeTeam", out var gh) && gh.ValueKind == JsonValueKind.Number;
            if (!played) continue;

            result.Played++;
            if (details.TryGetValue(matchId, out var detail) && detail.Status == MatchState.PlayedStatus)
            {
                result.DetailsCollected++;
                if (detail.ReceivedAt > lastUpdated) lastUpdated = detail.ReceivedAt;
            }
            if (lineups.Contains(matchId)) result.LineupsCollected++;
        }

        // Säsongen startar på hösten: matcher från juli och framåt hör till säsongen år/år+1
        if (firstMatch is { } first)
        {
            var startYear = first.Month >= 7 ? first.Year : first.Year - 1;
            result.Season = $"{startYear}/{(startYear + 1) % 100:00}";
        }

        result.Teams = teams.ToList();
        result.LastUpdatedAt = MatchState.ToSwedish(lastUpdated);
        if (result.Name.Length == 0) result.Name = $"Serie {competitionId}";
        return result;
    }

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    // Sparar dokumenten. En färdigspelad match skrivs aldrig över av en version som inte är det.
    public async Task<int> SaveAsync(IReadOnlyList<Document> documents)
    {
        var receivedAt = DateTime.UtcNow.ToString("O");
        var saved = 0;

        await using var connection = Open();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ApiDocuments (Path, Kind, CompetitionId, MatchId, MatchDate, Json, ReceivedAt)
            VALUES ($path, $kind, $competitionId, $matchId, $matchDate, $json, $receivedAt)
            ON CONFLICT (Path) DO UPDATE SET
                Kind = excluded.Kind,
                CompetitionId = excluded.CompetitionId,
                MatchId = excluded.MatchId,
                MatchDate = excluded.MatchDate,
                Json = excluded.Json,
                ReceivedAt = excluded.ReceivedAt
            WHERE NOT (
                ApiDocuments.Kind = 'match'
                AND json_extract(ApiDocuments.Json, '$.MatchStatus') = 4
                AND coalesce(json_extract(excluded.Json, '$.MatchStatus'), 0) <> 4
            )
            """;
        var pPath = command.Parameters.Add("$path", SqliteType.Text);
        var pKind = command.Parameters.Add("$kind", SqliteType.Text);
        var pCompetition = command.Parameters.Add("$competitionId", SqliteType.Integer);
        var pMatch = command.Parameters.Add("$matchId", SqliteType.Integer);
        var pDate = command.Parameters.Add("$matchDate", SqliteType.Text);
        var pJson = command.Parameters.Add("$json", SqliteType.Text);
        command.Parameters.AddWithValue("$receivedAt", receivedAt);

        foreach (var document in documents)
        {
            var meta = Describe(document.Path, document.Json);
            pPath.Value = document.Path;
            pKind.Value = meta.Kind;
            pCompetition.Value = (object?)meta.CompetitionId ?? DBNull.Value;
            pMatch.Value = (object?)meta.MatchId ?? DBNull.Value;
            pDate.Value = (object?)meta.MatchDate ?? DBNull.Value;
            pJson.Value = Sanitize(document.Json);
            saved += await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return saved;
    }

    private record Meta(string Kind, int? CompetitionId, int? MatchId, string? MatchDate);

    // Sökbara kolumner som plockas ut ur sökvägen och dokumentet
    private static Meta Describe(string path, string json)
    {
        var parts = path.Split('/');
        switch (parts)
        {
            case ["competitions", var c, "matches"] when int.TryParse(c, out var competitionId):
                return new Meta("matchlist", competitionId, null, null);

            case ["matches", var m] when int.TryParse(m, out var matchId):
            {
                int? competitionId = null;
                string? date = null;
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("CompetitionID", out var cid) && cid.TryGetInt32(out var id)) competitionId = id;
                    if (root.TryGetProperty("MatchDateTime", out var dt) && dt.ValueKind == JsonValueKind.String) date = dt.GetString();
                }
                return new Meta("match", competitionId, matchId, date);
            }

            case ["matches", var m, "lineups"] when int.TryParse(m, out var matchId):
                return new Meta("lineup", null, matchId, null);

            case ["players", _]:
                return new Meta("player", null, null, null);

            case ["seasons", _, "teams", _]:
                return new Meta("team", null, null, null);

            default:
                return new Meta("other", null, null, null);
        }
    }
}
