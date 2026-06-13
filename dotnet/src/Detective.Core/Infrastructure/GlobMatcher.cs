using System.Text;
using System.Text.RegularExpressions;

namespace Detective.Core.Infrastructure;

/// <summary>
/// Minimal glob matcher covering the subset used by Detective's file filters
/// (the same role <c>micromatch</c> plays in the TypeScript version): <c>*</c>,
/// <c>**</c>, <c>?</c> and leading <c>!</c> negation. A path is included when it
/// matches at least one positive pattern (or there are none) and matches no
/// negative pattern.
/// </summary>
public sealed class GlobMatcher
{
    private readonly List<Regex> _positive = new();
    private readonly List<Regex> _negative = new();
    private readonly bool _hasPositive;

    public GlobMatcher(IEnumerable<string> patterns)
    {
        foreach (var raw in patterns)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var negate = raw.StartsWith('!');
            var pattern = negate ? raw[1..] : raw;
            (negate ? _negative : _positive).Add(Compile(pattern));
        }
        _hasPositive = _positive.Count > 0;
    }

    public bool IsMatch(string path)
    {
        path = PathUtils.ToPosix(path);
        if (_negative.Any(r => r.IsMatch(path))) return false;
        if (!_hasPositive) return true;
        return _positive.Any(r => r.IsMatch(path));
    }

    private static Regex Compile(string glob)
    {
        glob = PathUtils.ToPosix(glob);
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        // "**/" matches any number of path segments (including none)
                        if (i + 2 < glob.Length && glob[i + 2] == '/')
                        {
                            sb.Append("(?:.*/)?");
                            i += 2;
                        }
                        else
                        {
                            sb.Append(".*");
                            i += 1;
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Compiled);
    }
}
