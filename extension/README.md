# Innebandystats – Chrome-plugin

Visar serietabell, poängliga, målvaktsliga, lagvy och matchanalys i Chromes sidopanel
medan du surfar på [stats.innebandy.se](https://stats.innebandy.se).

## Installera (utvecklarläge)

1. Öppna `chrome://extensions` och slå på **Utvecklarläge**.
2. Klicka **Läs in okomprimerat tillägg** och välj den här mappen (`extension/`).
3. Ladda om eventuella flikar med stats.innebandy.se som redan var öppna.
4. Gå in på en serie, t.ex. `https://stats.innebandy.se/sasong/44/serie/44031/spelprogram`,
   och klicka på pluginets ikon för att öppna sidopanelen.

## Så fungerar det

- `src/hook.js` körs i sidans egen JavaScript och fångar de JSON-svar som sajten själv
  hämtar från `api.innebandy.se`. `src/content.js` sparar dem i `chrome.storage.local`.
- När sidopanelen visar en serie laddas seriens matchlista och de spelade matchernas
  matchsidor i bakgrunden – en sida i taget med paus emellan, i en dold iframe
  (eller en bakgrundsflik om sajten inte tillåter iframe). Sajtens egen kod hämtar datan.
- Spelade matcher sparas permanent och hämtas bara en gång. Matchlistan laddas om
  när den är äldre än 10 minuter.
- Pluginet anropar aldrig API:t direkt.

## Synk till egen server

Allt pluginet fångar kan skickas till Innebandystats-appen (ASP.NET) så att statistiken
går att nå från mobilen.

1. Starta appen med en API-nyckel och en volym för datan:
   ```yaml
   services:
     innebandystats:
       image: ghcr.io/<konto>/innebandystats:latest
       ports: ["8080:8080"]
       environment:
         - Ingest__ApiKey=<lång slumpad nyckel>
       volumes:
         - innebandystats-data:/data
   volumes:
     innebandystats-data:
   ```
2. Synk är påslagen från start mot `https://innebandy.azurewebsites.net/` med standardnyckeln
   (samma som `Ingest:ApiKey` i appens `appsettings.json`). Vill du ändra något: högerklicka på
   pluginets ikon → **Alternativ**.
3. Klicka **Testa anslutningen**, och sedan **Skicka all insamlad data** för att skicka det
   pluginet redan samlat in.

På Azure App Service: sätt app-inställningarna `WEBSITES_ENABLE_APP_SERVICE_STORAGE=true` och
`Storage__Path=/home/data/innebandystats.db` så att databasen hamnar på den beständiga `/home`.

Dokument som inte kan skickas ligger kvar i en utkorg och skickas igen var femte minut.
Pluginets ikon får ett rött **!** när det senaste försöket misslyckades.

## Struktur

| Fil | Innehåll |
| --- | --- |
| `src/hook.js` | Fångar API-svar i sidan |
| `src/content.js` | Sparar data, kör bakgrundskön |
| `src/background.js` | Sidopanelens beteende, reserv via bakgrundsflik, synk |
| `src/sync.js` | Utkorg och uppladdning till `/api/ingest` |
| `options.html`, `src/options.js` | Inställningar för synk |
| `src/lib/stats.js` | Tabell, placeringar, matchanalys, spelar- och målvaktsstatistik (porterat från C#) |
| `src/lib/store.js` | Läser sparad data, sidrutter |
| `src/sidepanel.js` | Vyerna: serie, lag, match |

## Ännu inte porterat

Säsongsprojektion, laginsikter, lagjämförelse, ligans lagstatistik och spelarsidor.
