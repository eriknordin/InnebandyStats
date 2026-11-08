using Match = InnebandyStats.Models.Api.Match;

namespace InnebandyStats.Services;

// Matchens läge i förhållande till nu. API:t har ingen dokumenterad status för
// pågående matcher, så en ej färdigrapporterad match räknas som pågående från
// nedsläpp tills LiveWindow har passerat.
public static class MatchState
{
    public const int PlayedStatus = 4;
    public static readonly TimeSpan LiveWindow = TimeSpan.FromHours(3);

    private static readonly TimeZoneInfo? Stockholm = FindStockholm();

    private static TimeZoneInfo? FindStockholm()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm"); }
        catch (TimeZoneNotFoundException) { return null; }
        catch (InvalidTimeZoneException) { return null; }
    }

    // Matchtider från API:t är svensk lokal tid, oavsett serverns tidszon
    public static DateTime SwedishNow
    {
        get
        {
            var utc = DateTime.UtcNow;
            if (Stockholm != null)
                return TimeZoneInfo.ConvertTimeFromUtc(utc, Stockholm);

            // Reserv utan tidszonsdata: sommartid sista söndagen i mars till sista söndagen i oktober, 01:00 UTC
            var start = LastSunday(utc.Year, 3).AddHours(1);
            var end = LastSunday(utc.Year, 10).AddHours(1);
            return utc.AddHours(utc >= start && utc < end ? 2 : 1);
        }
    }

    public static DateTime SwedishToday => SwedishNow.Date;

    public static DateTime ToSwedish(DateTime utc) =>
        Stockholm != null
            ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Stockholm)
            : utc.AddHours(1);

    private static DateTime LastSunday(int year, int month)
    {
        var last = new DateTime(year, month, DateTime.DaysInMonth(year, month), 0, 0, 0, DateTimeKind.Utc);
        return last.AddDays(-(int)last.DayOfWeek);
    }

    public static bool IsPlayed(int status, int? goalsHome, int? goalsAway) =>
        status == PlayedStatus && goalsHome.HasValue && goalsAway.HasValue;

    public static bool IsPlayed(Match m) => IsPlayed(m.MatchStatus, m.GoalsHomeTeam, m.GoalsAwayTeam);

    public static bool IsLive(int status, int? goalsHome, int? goalsAway, DateTime start, DateTime now) =>
        !IsPlayed(status, goalsHome, goalsAway) && start <= now && now - start < LiveWindow;

    public static bool IsLive(Match m, DateTime now) =>
        IsLive(m.MatchStatus, m.GoalsHomeTeam, m.GoalsAwayTeam, m.MatchDateTime, now);

    public static bool IsUpcoming(Match m, DateTime now) => !IsPlayed(m) && m.MatchDateTime > now;
}
