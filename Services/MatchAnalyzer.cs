using System.Text.RegularExpressions;
using InnebandyStats.Models;
using InnebandyStats.Models.Api;
using Match = InnebandyStats.Models.Api.Match;

namespace InnebandyStats.Services;

// Räknar fram tidslinje, spelstyrka (PP/boxplay), målvaktsstatistik och skott ur en matchs händelser
public static class MatchAnalyzer
{
    // MatchEventTypeID i innebandy.se:s API
    public const int GoalType = 1;
    public const int PenaltyType = 2;
    public const int TimeoutHomeType = 3;
    public const int TimeoutAwayType = 4;
    public const int PenaltyShotGoalType = 5;
    public const int MissedPenaltyShotType = 6;
    public const int PeriodEndType = 9;
    public const int GoalieInType = 10;
    public const int GoalieOutType = 11;
    public const int EmptyNetGoalType = 12;

    private const int RegularPeriodSeconds = 20 * 60;
    private const int SkatersOnCourt = 5;
    private const int MaxPenalizedSkaters = 2;

    private static readonly Regex PenaltyMinutesRegex = new(@"(\d+(?:\s*\+\s*\d+)*)\s*min", RegexOptions.IgnoreCase);

    public static bool IsGoalType(int typeId) =>
        typeId is GoalType or PenaltyShotGoalType or EmptyNetGoalType;

    // Utvisningsminuter för statistiken och hur länge laget spelar med en man mindre.
    // 10 min är personligt straff och påverkar inte spelstyrkan.
    public static (int Minutes, int StrengthSeconds) ParsePenalty(string penaltyName)
    {
        if (penaltyName.Contains("Matchstraff", StringComparison.OrdinalIgnoreCase))
            return (5, 5 * 60);

        var match = PenaltyMinutesRegex.Match(penaltyName);
        if (!match.Success)
            return (2, 2 * 60);

        var parts = match.Groups[1].Value
            .Split('+')
            .Select(p => int.TryParse(p.Trim(), out var v) ? v : 0)
            .ToList();

        return (parts.Sum(), parts.Where(p => p != 10).Sum() * 60);
    }

    private class Penalty
    {
        public bool IsHome { get; init; }
        public int Start { get; init; }
        public int End { get; set; }
        public int StrengthSeconds { get; init; }
    }

    private record GoalieStint(bool IsHome, int PlayerID, string Name, int Start, int End);

    public static MatchAnalysis Analyze(Match match)
    {
        var analysis = new MatchAnalysis();
        var events = (match.Events ?? new List<MatchEvent>())
            .OrderBy(e => e.Period)
            .ThenBy(e => e.Minute)
            .ThenBy(e => e.Second)
            .ThenBy(e => e.MatchEventID)
            .ToList();

        // Periodlängder: periodslut om det finns, annars senaste händelse (minst 20 min för ordinarie perioder)
        var maxPeriod = events.Count > 0 ? Math.Max(1, events.Max(e => e.Period)) : 0;
        var offsets = new List<int>();
        var total = 0;
        for (var p = 1; p <= maxPeriod; p++)
        {
            var inPeriod = events.Where(e => e.Period == p).ToList();
            var periodEnd = inPeriod.Where(e => e.MatchEventTypeID == PeriodEndType)
                .Select(e => e.Minute * 60 + e.Second)
                .DefaultIfEmpty(0)
                .Max();
            var lastEvent = inPeriod.Select(e => e.Minute * 60 + e.Second).DefaultIfEmpty(0).Max();
            var length = periodEnd > 0 ? periodEnd : Math.Max(lastEvent, p <= 3 ? RegularPeriodSeconds : 0);

            offsets.Add(total);
            analysis.PeriodLengths.Add(length);
            total += length;
        }
        analysis.TotalSeconds = total;

        int TimeOf(MatchEvent e) =>
            (e.Period >= 1 && e.Period <= offsets.Count ? offsets[e.Period - 1] : 0) + e.Minute * 60 + e.Second;

        bool IsHome(MatchEvent e) => e.IsHomeTeam ?? e.MatchTeamID == match.HomeMatchTeamID;

        var penalties = new List<Penalty>();
        int PenalizedCount(bool home, int t) =>
            Math.Min(MaxPenalizedSkaters, penalties.Count(p => p.IsHome == home && p.Start <= t && t < p.End));

        var currentGoalie = new Dictionary<bool, (int PlayerID, string Name, int Start)>();
        var stints = new List<GoalieStint>();
        var goalsAgainst = new Dictionary<(bool IsHome, int PlayerID), int>();

        void CloseGoalie(bool home, int t)
        {
            if (currentGoalie.Remove(home, out var g))
                stints.Add(new GoalieStint(home, g.PlayerID, g.Name, g.Start, t));
        }

        foreach (var e in events)
        {
            var t = TimeOf(e);

            switch (e.MatchEventTypeID)
            {
                case GoalieInType when e.PlayerID > 0:
                {
                    var home = IsHome(e);
                    CloseGoalie(home, t);
                    currentGoalie[home] = (e.PlayerID, e.PlayerName ?? "", t);
                    continue;
                }
                case GoalieOutType:
                {
                    var home = IsHome(e);
                    if (currentGoalie.TryGetValue(home, out var g) && (e.PlayerID == 0 || g.PlayerID == e.PlayerID))
                        CloseGoalie(home, t);
                    continue;
                }
            }

            var item = new TimelineEvent
            {
                Period = e.Period,
                PeriodName = e.PeriodName,
                Minute = e.Minute,
                Second = e.Second,
                TimeSeconds = t,
                PlayerID = e.PlayerID,
                PlayerName = e.PlayerName ?? "",
                PlayerShirtNo = e.PlayerShirtNo,
                AssistID = e.PlayerAssistID,
                AssistName = e.PlayerAssistName ?? "",
                ScoreHome = e.GoalsHomeTeam,
                ScoreAway = e.GoalsAwayTeam
            };

            switch (e.MatchEventTypeID)
            {
                case GoalType:
                case PenaltyShotGoalType:
                case EmptyNetGoalType:
                {
                    item.Kind = e.MatchEventTypeID switch
                    {
                        PenaltyShotGoalType => TimelineEventKind.PenaltyShotGoal,
                        EmptyNetGoalType => TimelineEventKind.EmptyNetGoal,
                        _ => TimelineEventKind.Goal
                    };
                    item.IsHome = IsHome(e);

                    var own = SkatersOnCourt - PenalizedCount(item.IsHome, t);
                    var opponent = SkatersOnCourt - PenalizedCount(!item.IsHome, t);
                    item.Strength = own > opponent ? GoalStrength.PowerPlay
                        : own < opponent ? GoalStrength.ShortHanded
                        : GoalStrength.Even;

                    if (item.Strength == GoalStrength.PowerPlay)
                        EndMinorPenalty(penalties, !item.IsHome, t);

                    if (item.Kind != TimelineEventKind.EmptyNetGoal
                        && currentGoalie.TryGetValue(!item.IsHome, out var goalie))
                    {
                        var key = (!item.IsHome, goalie.PlayerID);
                        goalsAgainst[key] = goalsAgainst.GetValueOrDefault(key) + 1;
                    }
                    break;
                }
                case PenaltyType:
                {
                    item.Kind = TimelineEventKind.Penalty;
                    item.IsHome = IsHome(e);
                    item.Description = e.PenaltyName ?? "";
                    var (minutes, strengthSeconds) = ParsePenalty(e.PenaltyName ?? "");
                    item.PenaltyMinutes = minutes;

                    if (strengthSeconds > 0)
                    {
                        penalties.Add(new Penalty
                        {
                            IsHome = item.IsHome,
                            Start = t,
                            End = t + strengthSeconds,
                            StrengthSeconds = strengthSeconds
                        });
                        if (item.IsHome) analysis.AwayPpOpportunities++;
                        else analysis.HomePpOpportunities++;
                    }
                    break;
                }
                case MissedPenaltyShotType:
                    item.Kind = TimelineEventKind.MissedPenaltyShot;
                    item.IsHome = IsHome(e);
                    item.Description = e.MatchEventType;
                    break;
                case TimeoutHomeType:
                case TimeoutAwayType:
                    item.Kind = TimelineEventKind.Timeout;
                    item.IsHome = e.MatchEventTypeID == TimeoutHomeType;
                    item.Description = "Timeout";
                    break;
                default:
                    continue;
            }

            analysis.Events.Add(item);
        }

        foreach (var home in currentGoalie.Keys.ToList())
            CloseGoalie(home, total);

        MarkGameWinner(analysis, match);
        AddShots(analysis, match);
        AddGoalies(analysis, stints, goalsAgainst, offsets);

        return analysis;
    }

    // Ett powerplaymål avslutar motståndarens tidigast utgående 2-minutersutvisning
    private static void EndMinorPenalty(List<Penalty> penalties, bool penalizedIsHome, int t)
    {
        var penalty = penalties
            .Where(p => p.IsHome == penalizedIsHome && p.Start <= t && t < p.End
                        && p.StrengthSeconds is 2 * 60 or 4 * 60)
            .OrderBy(p => p.End)
            .FirstOrDefault();

        if (penalty == null) return;

        // 2+2: mål under första delen startar andra delen direkt
        penalty.End = penalty.StrengthSeconds == 4 * 60 && penalty.End - t > 2 * 60
            ? t + 2 * 60
            : t;
    }

    private static void MarkGameWinner(MatchAnalysis analysis, Match match)
    {
        var goals = analysis.Goals.ToList();
        var home = match.GoalsHomeTeam ?? goals.Count(g => g.IsHome);
        var away = match.GoalsAwayTeam ?? goals.Count(g => !g.IsHome);
        if (home == away) return;

        var winnerIsHome = home > away;
        var loserGoals = Math.Min(home, away);
        var winner = goals.FirstOrDefault(g => g.IsHome == winnerIsHome
            && (winnerIsHome ? g.ScoreHome : g.ScoreAway) == loserGoals + 1);
        if (winner != null)
            winner.IsGameWinner = true;
    }

    private static void AddShots(MatchAnalysis analysis, Match match)
    {
        if (match.ShotsOnGoal == null) return;

        analysis.Shots = match.ShotsOnGoal
            .Where(s => s.ShotsHomeTeam.HasValue || s.ShotsAwayTeam.HasValue)
            .OrderBy(s => s.Period)
            .Select(s => new PeriodShots
            {
                Period = s.Period,
                Home = s.ShotsHomeTeam ?? 0,
                Away = s.ShotsAwayTeam ?? 0
            })
            .ToList();

        // Inga registrerade skott alls räknas som saknad data
        if (analysis.Shots.All(s => s.Home == 0 && s.Away == 0))
            analysis.Shots.Clear();
    }

    private static void AddGoalies(MatchAnalysis analysis, List<GoalieStint> stints,
        Dictionary<(bool IsHome, int PlayerID), int> goalsAgainst, List<int> offsets)
    {
        foreach (var group in stints.GroupBy(s => (s.IsHome, s.PlayerID)))
        {
            var stats = new GoalieMatchStats
            {
                PlayerID = group.Key.PlayerID,
                IsHome = group.Key.IsHome,
                Name = group.First().Name,
                Seconds = group.Sum(s => s.End - s.Start),
                GoalsAgainst = goalsAgainst.GetValueOrDefault(group.Key),
                HasShots = analysis.HasShots
            };

            // Skott mot målvakten: motståndarens skott i perioden, fördelat efter speltid
            foreach (var shots in analysis.Shots)
            {
                var index = shots.Period - 1;
                if (index < 0 || index >= offsets.Count) continue;

                var start = offsets[index];
                var length = analysis.PeriodLengths[index];
                if (length <= 0) continue;

                var played = group.Sum(s => Math.Max(0, Math.Min(s.End, start + length) - Math.Max(s.Start, start)));
                var against = group.Key.IsHome ? shots.Away : shots.Home;
                stats.ShotsAgainst += against * (double)played / length;
            }

            if (stats.Seconds > 0)
                analysis.Goalies.Add(stats);
        }

        analysis.Goalies = analysis.Goalies
            .OrderByDescending(g => g.IsHome)
            .ThenByDescending(g => g.Seconds)
            .ToList();
    }
}
