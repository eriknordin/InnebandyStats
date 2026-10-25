using InnebandyStats.Models;
using Match = InnebandyStats.Models.Api.Match;

namespace InnebandyStats.Services;

// Poissonmodell över lagens målskapande: förväntade poäng i spelade matcher,
// simulering av resten av serien och sannolikheter för enskilda matcher
public static class SeasonSimulator
{
    public const int Simulations = 5000;
    private const int PlayedStatus = 4;
    private const int MaxGoals = 25;
    // Antal matcher med ligasnitt som läggs till varje lag så att tidiga
    // säsongsresultat inte ger extrema styrkor
    private const double PriorMatches = 3;

    private static bool IsPlayed(Match m) =>
        m.MatchStatus == PlayedStatus && m.GoalsHomeTeam.HasValue && m.GoalsAwayTeam.HasValue;

    private class Strength
    {
        public double Attack = 1;
        public double Defense = 1;
    }

    private record Model(double HomeAverage, double AwayAverage, Dictionary<string, Strength> Teams)
    {
        public (double Home, double Away) Lambdas(string home, string away)
        {
            var h = Teams.GetValueOrDefault(home) ?? new Strength();
            var a = Teams.GetValueOrDefault(away) ?? new Strength();
            return (HomeAverage * h.Attack * a.Defense, AwayAverage * a.Attack * h.Defense);
        }
    }

    private static Model BuildModel(List<Match> played, IEnumerable<string> teams)
    {
        var homeAverage = played.Count > 0 ? played.Average(m => m.GoalsHomeTeam!.Value) : 3;
        var awayAverage = played.Count > 0 ? played.Average(m => m.GoalsAwayTeam!.Value) : 3;
        // Undvik nollsnitt i serier med väldigt få mål
        homeAverage = Math.Max(0.5, homeAverage);
        awayAverage = Math.Max(0.5, awayAverage);
        var leagueAverage = (homeAverage + awayAverage) / 2;

        var strengths = new Dictionary<string, Strength>();
        foreach (var team in teams)
        {
            var games = played.Where(m => m.HomeTeam.Trim() == team || m.AwayTeam.Trim() == team).ToList();
            double goalsFor = 0, goalsAgainst = 0;
            foreach (var m in games)
            {
                var isHome = m.HomeTeam.Trim() == team;
                goalsFor += isHome ? m.GoalsHomeTeam!.Value : m.GoalsAwayTeam!.Value;
                goalsAgainst += isHome ? m.GoalsAwayTeam!.Value : m.GoalsHomeTeam!.Value;
            }

            var n = games.Count + PriorMatches;
            strengths[team] = new Strength
            {
                Attack = (goalsFor + PriorMatches * leagueAverage) / n / leagueAverage,
                Defense = (goalsAgainst + PriorMatches * leagueAverage) / n / leagueAverage
            };
        }

        return new Model(homeAverage, awayAverage, strengths);
    }

    private static double[] PoissonDistribution(double lambda)
    {
        var p = new double[MaxGoals + 1];
        p[0] = Math.Exp(-lambda);
        for (var k = 1; k <= MaxGoals; k++)
            p[k] = p[k - 1] * lambda / k;
        return p;
    }

    private static MatchOdds Odds(double homeLambda, double awayLambda)
    {
        var ph = PoissonDistribution(homeLambda);
        var pa = PoissonDistribution(awayLambda);
        double home = 0, draw = 0, away = 0;
        for (var i = 0; i <= MaxGoals; i++)
        for (var j = 0; j <= MaxGoals; j++)
        {
            var p = ph[i] * pa[j];
            if (i > j) home += p;
            else if (i == j) draw += p;
            else away += p;
        }

        var sum = home + draw + away;
        return new MatchOdds
        {
            HomeWin = home / sum,
            Draw = draw / sum,
            AwayWin = away / sum,
            ExpectedHomeGoals = homeLambda,
            ExpectedAwayGoals = awayLambda
        };
    }

    private static int SamplePoisson(Random random, double lambda)
    {
        // Knuths metod räcker för innebandyns målsnitt
        var limit = Math.Exp(-lambda);
        var k = 0;
        var p = random.NextDouble();
        while (p > limit && k < MaxGoals)
        {
            k++;
            p *= random.NextDouble();
        }
        return k;
    }

    public static MatchOdds GetMatchOdds(List<Match> matches, string homeTeam, string awayTeam)
    {
        var played = matches.Where(IsPlayed).ToList();
        var teams = matches.SelectMany(m => new[] { m.HomeTeam.Trim(), m.AwayTeam.Trim() }).Distinct();
        var model = BuildModel(played, teams);
        var (home, away) = model.Lambdas(homeTeam, awayTeam);
        return Odds(home, away);
    }

    public static SeasonProjection Project(int competitionId, List<Match> matches)
    {
        var played = matches.Where(IsPlayed).ToList();
        var table = InnebandyApiService.BuildTable(matches);
        var teamNames = table.Select(t => t.TeamName)
            .Concat(matches.SelectMany(m => new[] { m.HomeTeam.Trim(), m.AwayTeam.Trim() }))
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct()
            .ToList();
        var model = BuildModel(played, teamNames);

        // Ej spelade matcher, även de som passerat utan rapporterat resultat
        var remaining = matches
            .Where(m => !IsPlayed(m) && !string.IsNullOrWhiteSpace(m.HomeTeam) && !string.IsNullOrWhiteSpace(m.AwayTeam))
            .Select(m => (Home: m.HomeTeam.Trim(), Away: m.AwayTeam.Trim()))
            .ToList();

        var index = teamNames.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i);
        var teamCount = teamNames.Count;

        var basePoints = new int[teamCount];
        var baseFor = new int[teamCount];
        var baseAgainst = new int[teamCount];
        foreach (var t in table)
        {
            var i = index[t.TeamName];
            basePoints[i] = t.Points;
            baseFor[i] = t.GoalsFor;
            baseAgainst[i] = t.GoalsAgainst;
        }

        var currentRank = table.Select((t, i) => (t.TeamName, Rank: i + 1)).ToDictionary(x => x.TeamName, x => x.Rank);
        var projections = teamNames.Select(name => new TeamProjection
        {
            TeamName = name,
            Rank = currentRank.GetValueOrDefault(name, teamCount),
            PositionOdds = new double[teamCount],
            Attack = model.Teams[name].Attack,
            Defense = model.Teams[name].Defense
        }).ToList();

        foreach (var t in table)
        {
            var p = projections[index[t.TeamName]];
            p.Played = t.Played;
            p.Points = t.Points;
            p.GoalDiff = t.GoalDiff;
        }

        // Förväntade poäng i redan spelade matcher
        foreach (var m in played)
        {
            var home = m.HomeTeam.Trim();
            var away = m.AwayTeam.Trim();
            var (lh, la) = model.Lambdas(home, away);
            var odds = Odds(lh, la);
            projections[index[home]].ExpectedPoints += odds.HomeWin * 3 + odds.Draw;
            projections[index[away]].ExpectedPoints += odds.AwayWin * 3 + odds.Draw;
        }

        // Återstående schema
        foreach (var p in projections)
        {
            var opponents = remaining
                .Where(r => r.Home == p.TeamName || r.Away == p.TeamName)
                .Select(r => r.Home == p.TeamName ? r.Away : r.Home)
                .ToList();
            p.RemainingMatches = opponents.Count;
            p.RemainingOpponentRank = opponents.Count > 0
                ? opponents.Average(o => currentRank.GetValueOrDefault(o, teamCount))
                : null;
        }

        var lambdas = remaining
            .Select(r => (Home: index[r.Home], Away: index[r.Away], Lambda: model.Lambdas(r.Home, r.Away)))
            .ToList();

        // Samma seed ger samma prognos mellan sidladdningar
        var random = new Random(competitionId);
        var simulations = remaining.Count > 0 ? Simulations : 1;
        var points = new int[teamCount];
        var goalsFor = new int[teamCount];
        var goalsAgainst = new int[teamCount];
        var order = new int[teamCount];
        var totalPoints = new double[teamCount];
        var totalFinish = new double[teamCount];

        for (var s = 0; s < simulations; s++)
        {
            Array.Copy(basePoints, points, teamCount);
            Array.Copy(baseFor, goalsFor, teamCount);
            Array.Copy(baseAgainst, goalsAgainst, teamCount);

            foreach (var (home, away, lambda) in lambdas)
            {
                var hg = SamplePoisson(random, lambda.Home);
                var ag = SamplePoisson(random, lambda.Away);
                goalsFor[home] += hg; goalsAgainst[home] += ag;
                goalsFor[away] += ag; goalsAgainst[away] += hg;
                if (hg > ag) points[home] += 3;
                else if (hg < ag) points[away] += 3;
                else { points[home]++; points[away]++; }
            }

            for (var i = 0; i < teamCount; i++) order[i] = i;
            // Samma ordning som serietabellen; lika lag skiljs åt slumpmässigt
            var tiebreak = Enumerable.Range(0, teamCount).Select(_ => random.Next()).ToArray();
            Array.Sort(order, (a, b) =>
            {
                var c = points[b].CompareTo(points[a]);
                if (c != 0) return c;
                c = (goalsFor[b] - goalsAgainst[b]).CompareTo(goalsFor[a] - goalsAgainst[a]);
                if (c != 0) return c;
                c = goalsFor[b].CompareTo(goalsFor[a]);
                return c != 0 ? c : tiebreak[a].CompareTo(tiebreak[b]);
            });

            for (var pos = 0; pos < teamCount; pos++)
            {
                var team = order[pos];
                projections[team].PositionOdds[pos]++;
                totalFinish[team] += pos + 1;
            }
            for (var i = 0; i < teamCount; i++)
                totalPoints[i] += points[i];
        }

        for (var i = 0; i < teamCount; i++)
        {
            var p = projections[i];
            for (var pos = 0; pos < teamCount; pos++)
                p.PositionOdds[pos] /= simulations;
            p.ProjectedPoints = totalPoints[i] / simulations;
            p.AverageFinish = totalFinish[i] / simulations;
        }

        return new SeasonProjection
        {
            CompetitionId = competitionId,
            Simulations = simulations,
            RemainingMatches = remaining.Count,
            HomeGoalsAverage = model.HomeAverage,
            AwayGoalsAverage = model.AwayAverage,
            Teams = projections.OrderBy(p => p.Rank).ThenBy(p => p.TeamName).ToList()
        };
    }
}
