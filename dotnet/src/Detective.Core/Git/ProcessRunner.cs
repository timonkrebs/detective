using System.Diagnostics;
using System.Text;

namespace Detective.Core.Git;

internal static class ProcessRunner
{
    public static (int ExitCode, string StdOut, string StdErr) Run(
        string fileName, IEnumerable<string> args, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.Append(e.Data).Append('\n'); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.Append(e.Data).Append('\n'); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        p.WaitForExit();
        return (p.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
