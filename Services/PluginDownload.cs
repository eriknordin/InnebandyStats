using System.IO.Compression;
using System.Text.Json;

namespace InnebandyStats.Services;

// Chrome-pluginet: packas från mappen extension/ till en zip vid nedladdning, så att
// nedladdningen alltid är samma version som appen kör.
public static class PluginDownload
{
    public static string Folder(IWebHostEnvironment env) => Path.Combine(env.ContentRootPath, "extension");

    // Versionen ur manifest.json, eller null om pluginet inte finns med
    public static string? Version(IWebHostEnvironment env)
    {
        var manifest = Path.Combine(Folder(env), "manifest.json");
        if (!File.Exists(manifest)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
    }

    public static void MapPluginDownload(this WebApplication app)
    {
        app.MapGet("/plugin/Innebandystats-plugin.zip", (IWebHostEnvironment env) =>
        {
            var folder = Folder(env);
            if (!File.Exists(Path.Combine(folder, "manifest.json")))
                return Results.NotFound("Pluginet finns inte med i den här installationen.");

            // manifest.json ligger i zip-filens rot: "Extrahera alla" ger då en enda mapp att välja i Chrome
            var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    var name = Path.GetRelativePath(folder, file).Replace('\\', '/');
                    if (name.Equals("README.md", StringComparison.OrdinalIgnoreCase)) continue;
                    zip.CreateEntryFromFile(file, name, CompressionLevel.Optimal);
                }
            }
            buffer.Position = 0;
            return Results.File(buffer, "application/zip", "Innebandystats-plugin.zip");
        });
    }
}
