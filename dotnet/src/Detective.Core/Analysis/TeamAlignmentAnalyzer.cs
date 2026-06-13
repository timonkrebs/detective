using Detective.Core.Git;
using Detective.Core.Infrastructure;
using Detective.Core.Model;

namespace Detective.Core.Analysis;

/// <summary>
/// Attributes per-module change volume to teams (or individual users), revealing
/// whether team boundaries align with module boundaries. Port of
/// <c>team-alignment.ts</c>.
/// </summary>
public static class TeamAlignmentAnalyzer
{
    private const string UnknownTeam = "unknown";

    public static TeamAlignmentResult Calc(string log, Config config, Limits limits, bool byUser, bool demoMode)
    {
        var displayModules = config.Scopes.OrderBy(s => s, StringComparer.Ordinal).ToList();
        var modules = displayModules.Select(PathUtils.NormalizeFolder).ToList();
        var teams = config.Teams;

        var userToTeam = InitUserToTeam(teams);
        var result = InitResult(displayModules, teams.Keys);

        var actualTeams = new HashSet<string>();
        var userKeyToDisplay = new Dictionary<string, string>();
        var demoUsers = new[] { "Max Muster", "John Doe", "Jane Doe", "Maria Muster" };
        var count = 0;

        var parseOptions = new ParseOptions { Limits = limits, Filter = config.Filter };

        GitLogParser.Parse(log, entry =>
        {
            var userName = entry.Header.UserName;
            if (demoMode)
            {
                count++;
                userName = demoUsers[(count - 1) % demoUsers.Length];
            }

            if (config.Aliases.TryGetValue(userName, out var alias)) userName = alias;

            var emailLower = (entry.Header.Email ?? string.Empty).ToLowerInvariant();
            var stableUserKey = byUser
                ? (demoMode ? userName : (string.IsNullOrEmpty(emailLower) ? userName : emailLower))
                : userName;

            if (!userKeyToDisplay.ContainsKey(stableUserKey))
                userKeyToDisplay[stableUserKey] = userName;

            var key = byUser ? stableUserKey : CalcTeamKey(userName, userToTeam);
            actualTeams.Add(key);

            foreach (var change in entry.Body)
            {
                for (var i = 0; i < modules.Count; i++)
                {
                    if (change.Path.StartsWith(modules[i], StringComparison.Ordinal))
                    {
                        var changes = result.Modules[displayModules[i]].Changes;
                        changes[key] = (changes.TryGetValue(key, out var cur) ? cur : 0)
                                       + change.LinesAdded + change.LinesRemoved;
                        break;
                    }
                }
            }
        }, parseOptions);

        if (byUser)
        {
            foreach (var module in result.Modules.Keys.ToList())
            {
                var changes = result.Modules[module].Changes;
                var remapped = new Dictionary<string, int>();
                foreach (var (stableKey, value) in changes)
                {
                    var baseName = userKeyToDisplay.TryGetValue(stableKey, out var d) ? d : stableKey;
                    remapped[baseName] = (remapped.TryGetValue(baseName, out var c) ? c : 0) + value;
                }
                result.Modules[module].Changes = remapped;
            }
            result.Teams = actualTeams
                .Select(k => userKeyToDisplay.TryGetValue(k, out var d) ? d : k)
                .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        }
        else
        {
            result.Teams = actualTeams.OrderBy(x => x, StringComparer.Ordinal).ToList();
        }

        return result;
    }

    private static string CalcTeamKey(string userName, Dictionary<string, string> userToTeam) =>
        userToTeam.TryGetValue(userName, out var team) ? team : UnknownTeam;

    private static Dictionary<string, string> InitUserToTeam(Dictionary<string, List<string>> teams)
    {
        var map = new Dictionary<string, string>();
        foreach (var (teamName, members) in teams)
            foreach (var user in members)
                map[user] = teamName;
        return map;
    }

    private static TeamAlignmentResult InitResult(List<string> modules, IEnumerable<string> teams)
    {
        var sorted = teams.OrderBy(x => x, StringComparer.Ordinal).ToList();
        sorted.Add(UnknownTeam);
        var result = new TeamAlignmentResult { Teams = sorted };
        foreach (var module in modules) result.Modules[module] = new ModuleDetails();
        return result;
    }
}
