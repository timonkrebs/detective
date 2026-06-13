using Detective.Core.Git;
using Detective.Core.Model;
using Xunit;

namespace Detective.Tests;

public class GitLogParserTests
{
    // Mirrors the cached format produced by GitLogProvider:
    //   "%an <%ae>,%aI%x09%H,%s"  (with literal surrounding quotes)
    private const string Log =
        "\"Jane Doe <jane@x.io>,2024-03-01T12:00:00+00:00\tAAA,feat: a\"\n" +
        "10\t2\tsrc/a/File.cs\n" +
        "3\t0\tsrc/b/Other.cs\n" +
        "\n" +
        "\"John <john@x.io>,2024-03-02T12:00:00+00:00\tBBB,refactor\"\n" +
        "2\t0\tsrc/a/{Old => New}/File.cs\n";

    [Fact]
    public void Parses_headers_bodies_and_dates()
    {
        var entries = new List<LogEntry>();
        GitLogParser.Parse(Log, entries.Add);

        Assert.Equal(2, entries.Count);

        var first = entries[0];
        Assert.Equal("Jane Doe", first.Header.UserName);
        Assert.Equal("jane@x.io", first.Header.Email);
        Assert.Equal(2024, first.Header.Date.Year);
        Assert.Equal(2, first.Body.Count);
        Assert.Equal("src/a/File.cs", first.Body[0].Path);
        Assert.Equal(10, first.Body[0].LinesAdded);
        Assert.Equal(2, first.Body[0].LinesRemoved);
    }

    [Fact]
    public void Resolves_rename_syntax()
    {
        var entries = new List<LogEntry>();
        GitLogParser.Parse(Log, entries.Add);
        Assert.Equal("src/a/New/File.cs", entries[1].Body[0].Path);
    }

    [Fact]
    public void Applies_file_filter_with_negation()
    {
        var entries = new List<LogEntry>();
        var options = new ParseOptions
        {
            Filter = new Filter { Files = new() { "**/*.cs", "!**/b/**" } }
        };
        GitLogParser.Parse(Log, entries.Add, options);

        Assert.Single(entries[0].Body);
        Assert.Equal("src/a/File.cs", entries[0].Body[0].Path);
    }

    [Fact]
    public void Honors_commit_limit()
    {
        var entries = new List<LogEntry>();
        GitLogParser.Parse(Log, entries.Add, new ParseOptions { Limits = new Limits { LimitCommits = 1 } });
        Assert.Single(entries);
    }
}
