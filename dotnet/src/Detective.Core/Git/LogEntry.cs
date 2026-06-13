namespace Detective.Core.Git;

public sealed class LogHeader
{
    public string UserName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public DateTimeOffset Date { get; set; } = DateTimeOffset.UnixEpoch;
}

public sealed class LogBodyEntry
{
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public string Path { get; set; } = string.Empty;
}

public sealed class LogEntry
{
    public LogHeader Header { get; set; } = new();
    public List<LogBodyEntry> Body { get; set; } = new();
}
