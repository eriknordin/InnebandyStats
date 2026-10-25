using System.Text.RegularExpressions;
using InnebandyStats.Models;

namespace InnebandyStats.Services;

// Räknar fram matchlägen, trender, bredd, disciplin och trupp för ett lag ur seriedatan
public static class TeamInsightsCalculator
{
    private const int TrendWindow = 5;
    private const int NewcomerWindow = 3;
    private static readonly Regex PenaltyMinutesPart = new(@"\(?\s*\d+(?:\s*\+\s*\d+)*\s*min\.?\s*\)?", RegexOptions.IgnoreCase);

    public static TeamInsights Calculate(CompetitionData data, string teamName, List<TeamTableEntry> table,
        List<PlayerStanding> standings)
    {
        var teamPlayed = data.Played
            .Where(pm => pm.HomeTeam == teamName || pm.AwayTeam == teamName)
            .Where(pm => pm.Match.GoalsHomeTeam.HasValue && pm.Match.GoalsAwayTeam.HasValue)
            .OrderBy(pm => pm.Match.MatchDateTime)
            .ToList();

        var insights = new TeamInsights
        {
            GameStates = GameStates(teamPlayed, teamName),
            Trend = Trend(teamPlayed, teamName),
            Depth = Depth(standings, teamName),
            Discipline = Discipline(data, teamPlayed, teamName),
            Squad = Squad(teamPlayed, teamName)
        };

        // Facit mot tabellens tredjedelar (lagets egen placering räknas inte)
        var others = table.Select(t => t.TeamName).Where(t => t != teamName).ToList();
        if (others.Count >= 3)
        {
            var third = (int)Math.Ceiling(others.Count / 3.0);
            var group = others.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i / third);
            foreach (var pm in teamPlayed)
            {
                var (isHome, gf, ga) = Perspective(pm, teamName);
                var opponent = isHome ? pm.AwayTeam : pm.HomeTeam;
                if (!group.TryGetValue(opponent, out var g)) continue;
                (g == 0 ? insights.VsTop : g == 1 ? insights.VsMiddle : insights.VsBottom).Add(gf, ga);
            }
        }

        return insights;
    }

    private static (bool IsHome, int GoalsFor, int GoalsAgainst) Perspective(PlayedMatch pm, string teamName)
    {
        var isHome = pm.HomeTeam == teamName;
        var home = pm.Match.GoalsHomeTeam!.Value;
        var away = pm.Match.GoalsAwayTeam!.Value;
        return (isHome, isHome ? home : away, isHome ? away : home);
    }

    private static TeamMatchResult ToResult(PlayedMatch pm, string teamName)
    {
        var (isHome, gf, ga) = Perspective(pm, teamName);
        return new TeamMatchResult
        {
            MatchID = pm.Match.MatchID,
            MatchDateTime = pm.Match.MatchDateTime,
            Opponent = isHome ? pm.AwayTeam : pm.HomeTeam,
            IsHome = isHome,
            GoalsFor = gf,
            GoalsAgainst = ga,
            MatchStatus = pm.Match.MatchStatus,
            RoundName = pm.Match.RoundName,
            Round = pm.Match.Round
        };
    }

    public static GameStateStats GameStates(List<PlayedMatch> teamPlayed, string teamName)
    {
        var stats = new GameStateStats();

        foreach (var pm in teamPlayed)
        {
            var (isHome, gf, ga) = Perspective(pm, teamName);
            var goals = pm.Analysis.Goals.OrderBy(g => g.TimeSeconds).ToList();

            // Matchlägen kräver att alla mål finns som händelser
            if (goals.Count(g => g.IsHome == isHome) != gf || goals.Count(g => g.IsHome != isHome) != ga)
                continue;

            stats.MatchesAnalyzed++;

            if (goals.Count > 0)
                (goals[0].IsHome == isHome ? stats.ScoredFirst : stats.ConcededFirst).Add(gf, ga);

            int Diff(int period) =>
                goals.Count(g => g.Period <= period && g.IsHome == isHome) - goals.Count(g => g.Period <= period && g.IsHome != isHome);

            var d1 = Diff(1);
            (d1 > 0 ? stats.LeadAfterP1 : d1 == 0 ? stats.TiedAfterP1 : stats.TrailAfterP1).Add(gf, ga);
            var d2 = Diff(2);
            (d2 > 0 ? stats.LeadAfterP2 : d2 == 0 ? stats.TiedAfterP2 : stats.TrailAfterP2).Add(gf, ga);

            if (Math.Abs(gf - ga) == 1) stats.OneGoalGames.Add(gf, ga);
            if (Math.Abs(gf - ga) >= 4) stats.Blowouts.Add(gf, ga);

            // Största ledning och underläge under matchen
            int diff = 0, maxLead = 0, maxDeficit = 0;
            foreach (var g in goals)
            {
                diff += g.IsHome == isHome ? 1 : -1;
                maxLead = Math.Max(maxLead, diff);
                maxDeficit = Math.Max(maxDeficit, -diff);
            }

            if (maxDeficit > 0 && gf >= ga)
                stats.Comebacks.Add(new GameSwing { Match = ToResult(pm, teamName), Margin = maxDeficit });
            if (maxLead > 0 && gf <= ga)
                stats.BlownLeads.Add(new GameSwing { Match = ToResult(pm, teamName), Margin = maxLead });

            // Svar inom två minuter efter insläppt mål
            foreach (var conceded in goals.Where(g => g.IsHome != isHome))
            {
                stats.GoalsConceded++;
                if (goals.Any(g => g.IsHome == isHome && g.TimeSeconds > conceded.TimeSeconds
                                   && g.TimeSeconds - conceded.TimeSeconds <= GameStateStats.ResponseSeconds))
                    stats.QuickResponses++;
            }
        }

        stats.Comebacks = stats.Comebacks.OrderByDescending(c => c.Margin).ThenByDescending(c => c.Match.MatchDateTime).ToList();
        stats.BlownLeads = stats.BlownLeads.OrderByDescending(c => c.Margin).ThenByDescending(c => c.Match.MatchDateTime).ToList();
        return stats;
    }

    private static List<TrendPoint> Trend(List<PlayedMatch> teamPlayed, string teamName)
    {
        var points = new List<TrendPoint>();
        foreach (var pm in teamPlayed)
        {
            var (isHome, gf, ga) = Perspective(pm, teamName);
            points.Add(new TrendPoint
            {
                MatchID = pm.Match.MatchID,
                MatchDateTime = pm.Match.MatchDateTime,
                Opponent = isHome ? pm.AwayTeam : pm.HomeTeam,
                GoalsFor = gf,
                GoalsAgainst = ga
            });

            var window = points.TakeLast(TrendWindow).ToList();
            points[^1].RollingFor = window.Average(p => p.GoalsFor);
            points[^1].RollingAgainst = window.Average(p => p.GoalsAgainst);
        }
        return points;
    }

    private static DepthStats Depth(List<PlayerStanding> standings, string teamName)
    {
        var players = standings.Where(p => p.Team == teamName).ToList();
        var teamGoals = players.Sum(p => p.Goals);
        var top = players.Where(p => p.Goals > 0)
            .OrderByDescending(p => p.Goals).ThenBy(p => p.Name)
            .Take(3)
            .ToList();

        return new DepthStats
        {
            TeamGoals = teamGoals,
            Scorers = players.Count(p => p.Goals > 0),
            PointGetters = players.Count(p => p.Points > 0),
            TopThreeGoalShare = teamGoals > 0 ? top.Sum(p => p.Goals) * 100.0 / teamGoals : null,
            TopScorers = top.Select(p => (p.PlayerID, p.Name, p.Goals)).ToList()
        };
    }

    // Utvisningstyp utan minuter, t.ex. "Slag 2 min" → "Slag"
    public static string PenaltyType(string description)
    {
        var name = PenaltyMinutesPart.Replace(description, " ").Trim(' ', ',', '-', '–', '.');
        name = Regex.Replace(name, @"\s{2,}", " ");
        return name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : "Okänd";
    }

    private static DisciplineStats Discipline(CompetitionData data, List<PlayedMatch> teamPlayed, string teamName)
    {
        var stats = new DisciplineStats { Matches = teamPlayed.Count };
        var types = new Dictionary<string, int>();
        var offenders = new Dictionary<int, (string Name, int Minutes)>();

        foreach (var pm in teamPlayed)
        {
            var isHome = pm.HomeTeam == teamName;
            foreach (var e in pm.Analysis.Events.Where(e => e.Kind == TimelineEventKind.Penalty && e.IsHome == isHome))
            {
                stats.Penalties++;
                stats.PenaltyMinutes += e.PenaltyMinutes;
                stats.ByPeriod[Math.Clamp(e.Period, 1, 4) - 1]++;

                var type = PenaltyType(e.Description);
                types[type] = types.GetValueOrDefault(type) + 1;

                if (e.PlayerID > 0)
                {
                    var current = offenders.GetValueOrDefault(e.PlayerID, (e.PlayerName.Trim(), 0));
                    offenders[e.PlayerID] = (current.Item1, current.Item2 + e.PenaltyMinutes);
                }
            }
        }

        // Ligasnitt: utvisningar per lag och match
        var leagueMatches = data.Played.Count * 2;
        if (leagueMatches > 0)
            stats.LeaguePenaltiesPerMatch = data.Played
                .Sum(pm => pm.Analysis.Events.Count(e => e.Kind == TimelineEventKind.Penalty)) / (double)leagueMatches;

        stats.CommonTypes = types.OrderByDescending(t => t.Value).ThenBy(t => t.Key)
            .Take(5).Select(t => (t.Key, t.Value)).ToList();
        stats.TopOffenders = offenders.OrderByDescending(o => o.Value.Minutes).ThenBy(o => o.Value.Name)
            .Take(3).Select(o => (o.Key, o.Value.Name, o.Value.Minutes)).ToList();
        return stats;
    }

    private static SquadStats Squad(List<PlayedMatch> teamPlayed, string teamName)
    {
        var stats = new SquadStats { NewcomerWindow = NewcomerWindow };
        var appearances = new List<(Models.Api.LineupPlayer Player, DateTime Date)>();
        var matchesWithLineup = new List<DateTime>();

        foreach (var pm in teamPlayed)
        {
            if (pm.Lineup == null) continue;
            var players = pm.HomeTeam == teamName ? pm.Lineup.HomeTeamPlayers : pm.Lineup.AwayTeamPlayers;
            if (players.Count == 0) continue;

            matchesWithLineup.Add(pm.Match.MatchDateTime);
            appearances.AddRange(players.Select(p => (p, pm.Match.MatchDateTime)));
        }

        stats.MatchesWithLineup = matchesWithLineup.Count;
        if (appearances.Count == 0) return stats;

        var byPlayer = appearances.GroupBy(a => a.Player.PlayerID).ToList();
        stats.PlayersUsed = byPlayer.Count;

        var withAge = appearances.Where(a => a.Player.Age > 0).ToList();
        if (withAge.Count > 0)
        {
            stats.AverageAge = withAge.Average(a => a.Player.Age);
            var distinct = withAge.GroupBy(a => a.Player.PlayerID).Select(g => g.Last().Player).ToList();
            var youngest = distinct.OrderBy(p => p.Age).ThenByDescending(p => p.BirthYear).First();
            var oldest = distinct.OrderByDescending(p => p.Age).ThenBy(p => p.BirthYear).First();
            stats.Youngest = (youngest.PlayerID, youngest.Name.Trim(), youngest.Age);
            stats.Oldest = (oldest.PlayerID, oldest.Name.Trim(), oldest.Age);
        }

        // Nya spelare kräver några matcher bakåt att jämföra mot
        if (matchesWithLineup.Count > NewcomerWindow + 1)
        {
            var windowStart = matchesWithLineup[^NewcomerWindow];
            stats.Newcomers = byPlayer
                .Select(g => (g.Key, Name: g.First().Player.Name.Trim(), First: g.Min(a => a.Date)))
                .Where(p => p.First >= windowStart)
                .OrderBy(p => p.First).ThenBy(p => p.Name)
                .ToList();
        }

        return stats;
    }
}
