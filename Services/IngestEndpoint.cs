using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace InnebandyStats.Services;

// POST /api/ingest – tar emot API-dokument från Chrome-pluginet.
// Kräver headern X-Api-Key med värdet från konfigurationen Ingest:ApiKey.
public static class IngestEndpoint
{
    private const int MaxDocumentsPerRequest = 500;

    public record IngestDocument(string Path, JsonElement Data);
    public record IngestRequest(List<IngestDocument>? Documents);

    public static void MapIngest(this WebApplication app)
    {
        app.MapPost("/api/ingest", async (
            HttpRequest request,
            IngestRequest body,
            ApiDocumentStore store,
            IMemoryCache cache,
            IConfiguration config,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("Ingest");
            var expectedKey = config["Ingest:ApiKey"];
            if (string.IsNullOrWhiteSpace(expectedKey))
                return Results.Problem("Ingest är inte konfigurerat (Ingest:ApiKey saknas).", statusCode: 503);

            var providedKey = request.Headers["X-Api-Key"].ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(providedKey), Encoding.UTF8.GetBytes(expectedKey)))
                return Results.Unauthorized();

            var documents = body.Documents ?? new List<IngestDocument>();
            if (documents.Count > MaxDocumentsPerRequest)
                return Results.BadRequest($"Max {MaxDocumentsPerRequest} dokument per anrop.");

            var valid = new List<ApiDocumentStore.Document>();
            var rejected = new List<string>();
            foreach (var document in documents)
            {
                var path = ApiDocumentStore.NormalizePath(document.Path);
                if (path == null || document.Data.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                {
                    rejected.Add(document.Path ?? "");
                    continue;
                }
                valid.Add(new ApiDocumentStore.Document(path, document.Data.GetRawText()));
            }

            var saved = valid.Count > 0 ? await store.SaveAsync(valid) : 0;

            // Beräknad seriedata bygger på dokumenten – släpp cachen så att nästa sidvisning blir aktuell
            if (saved > 0 && cache is MemoryCache memoryCache)
                memoryCache.Clear();

            logger.LogInformation("Tog emot {Received} dokument, sparade {Saved}, avvisade {Rejected}",
                documents.Count, saved, rejected.Count);
            return Results.Ok(new { received = documents.Count, saved, rejected });
        }).DisableAntiforgery();

        // POST /api/series/{id}/clear – rensar all synkad data för serien
        app.MapPost("/api/series/{competitionId:int}/clear", async (
            int competitionId, ApiDocumentStore store, IMemoryCache cache, ILoggerFactory loggerFactory) =>
        {
            var deleted = await store.DeleteCompetitionAsync(competitionId);
            if (cache is MemoryCache memoryCache)
                memoryCache.Clear();
            loggerFactory.CreateLogger("Ingest").LogInformation("Rensade serie {CompetitionId}: {Deleted} dokument", competitionId, deleted);
            return Results.Ok(new { deleted });
        }).DisableAntiforgery();
    }
}
