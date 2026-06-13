using System.Globalization;
using System.Text.RegularExpressions;
using Detective.Core.Infrastructure;
using Detective.Core.Model;

namespace Detective.Core.Git;

public sealed class ParseOptions
{
    public Limits Limits { get; set; } = Limits.None;
    public Filter? Filter { get; set; }
}

/// <summary>
/// Streaming parser for the cached <c>git log --numstat</c> output. This is a
/// direct port of the original TypeScript state machine (header / body / skip),
/// including rename handling and message/file filtering.
/// </summary>
public static class GitLogParser
{
    private static readonly Regex RenameRegex =
        new(@"(.*?)\{(.*?) => (.*?)\}(.*)", RegexOptions.Compiled);

    private enum State { Header, Body, Skip }

    public static void Parse(string log, Action<LogEntry> callback, ParseOptions? options = null)
    {
        options ??= new ParseOptions();
        var limits = options.Limits;
        var fileFilter = new GlobMatcher(
            options.Filter?.Files is { Count: > 0 } f ? f : new List<string> { "**/*.cs" });
        var logExcludes = options.Filter?.Logs ?? new List<string>();

        var dateLimit = limits.LimitMonths is int m
            ? DateTimeOffset.Now.AddMonths(-m)
            : DateTimeOffset.UnixEpoch;

        var renameMap = new Dictionary<string, string>();
        var header = new LogHeader();
        var body = new List<LogBodyEntry>();
        var state = State.Header;
        var count = 0;

        foreach (var line in SplitLines(log))
        {
            if (ContainsExcluded(line, logExcludes))
            {
                state = State.Skip;
            }
            else if (state == State.Header)
            {
                count++;
                if (limits.LimitCommits is int lc && count > lc) return;

                header = ParseHeader(line);
                state = header.Date < dateLimit ? State.Skip : State.Body;
            }
            else if (state == State.Body)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    callback(new LogEntry { Header = header, Body = body });
                    body = new List<LogBodyEntry>();
                    state = State.Header;
                }
                else if (line.Split('\t').Length < 3)
                {
                    // No blank separator before the next commit (e.g. empty commit).
                    header = ParseHeader(line);
                }
                else
                {
                    var entry = ParseBodyEntry(line, renameMap);
                    if (fileFilter.IsMatch(entry.Path)) body.Add(entry);
                }
            }
            else // Skip
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    body = new List<LogBodyEntry>();
                    state = State.Header;
                }
            }
        }

        if (body.Count > 0)
            callback(new LogEntry { Header = header, Body = body });
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        // Mirror getNextLine: split on \n, drop \r.
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                yield return text.Substring(start, i - start).Replace("\r", string.Empty);
                start = i + 1;
            }
        }
        if (start < text.Length)
            yield return text.Substring(start).Replace("\r", string.Empty);
    }

    private static bool ContainsExcluded(string line, List<string> excludes) =>
        excludes.Any(e => !string.IsNullOrEmpty(e) && line.Contains(e));

    private static LogBodyEntry ParseBodyEntry(string line, Dictionary<string, string> renameMap)
    {
        var parts = line.Split('\t');
        int.TryParse(parts[0], out var added);
        int.TryParse(parts[1], out var removed);
        var filePath = HandleRenames(parts.Length > 2 ? parts[2] : string.Empty, renameMap);
        return new LogBodyEntry { LinesAdded = added, LinesRemoved = removed, Path = filePath };
    }

    private static string PathJoin(params string[] parts) =>
        Regex.Replace(string.Join("/", parts), "/{2,}", "/").TrimEnd('/');

    private static string HandleRenames(string filePath, Dictionary<string, string> renameMap)
    {
        var match = RenameRegex.Match(filePath);
        if (match.Success)
        {
            var start = match.Groups[1].Value;
            var before = match.Groups[2].Value;
            var after = match.Groups[3].Value;
            var end = match.Groups[4].Value;

            var from = PathJoin(start, before, end);
            var to = PathJoin(start, after, end);

            renameMap[from] = renameMap.TryGetValue(to, out var existing) ? existing : to;
            filePath = to;
        }
        return renameMap.TryGetValue(filePath, out var mapped) ? mapped : filePath;
    }

    private static LogHeader ParseHeader(string line)
    {
        var info = line.Split('\t')[0];
        var parts = info.Split(',').ToList();
        var iso = parts[^1];
        parts.RemoveAt(parts.Count - 1);
        var date = ToDate(iso);
        var fullUserName = string.Join(",", parts);
        var userParts = fullUserName.Split('<');
        var userName = CleanUserName(userParts[0]);
        var email = userParts.Length > 1 ? CleanEmail(userParts[1]) : string.Empty;
        return new LogHeader { UserName = userName, Email = email, Date = date };
    }

    private static DateTimeOffset ToDate(string iso)
    {
        iso = iso.Trim();
        if (iso.EndsWith('"')) iso = iso[..^1];
        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : DateTimeOffset.UnixEpoch;
    }

    private static string CleanEmail(string part) =>
        part.EndsWith('>') ? part[..^1] : part;

    private static string CleanUserName(string userName)
    {
        userName = userName.Trim();
        if (userName.StartsWith('"')) userName = userName[1..];
        return userName;
    }
}
