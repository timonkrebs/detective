using System.Diagnostics;

namespace Detective.Tests;

/// <summary>Creates a disposable, isolated git repository for integration tests.</summary>
public sealed class TempGitRepo : IDisposable
{
    public string Path { get; }

    public TempGitRepo()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "det-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        Git("init", "-q");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test User");
        Git("config", "commit.gpgsign", "false");
    }

    public void Write(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Commit(string message)
    {
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
    }

    public void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Path, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {p.StandardError.ReadToEnd()}");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}
