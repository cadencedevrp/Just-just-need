using System.Text;
using System.Text.RegularExpressions;

namespace StormsBackupSystem.Core;

/// <summary>
/// Decides whether a file or folder should be left out of the backup.
/// <list type="bullet">
/// <item><c>cache</c> — any file or folder named "cache", anywhere.</item>
/// <item><c>*.log</c> — any file/folder whose name matches the wildcard.</item>
/// <item><c>resources/[local]/temp</c> — a path relative to the source folder (wildcards allowed, <c>**</c> = any depth).</item>
/// </list>
/// Lines starting with # are comments. Matching is case-insensitive.
/// </summary>
public sealed class ExcludeMatcher
{
    private readonly List<Regex> _nameRules = new();
    private readonly List<Regex> _pathRules = new();

    public ExcludeMatcher(IEnumerable<string> patterns)
    {
        foreach (var raw in patterns)
        {
            var pattern = raw.Trim();
            if (pattern.Length == 0 || pattern.StartsWith('#'))
            {
                continue;
            }

            pattern = pattern.Replace('\\', '/').Trim('/');
            if (pattern.Length == 0)
            {
                continue;
            }

            var regex = new Regex(
                "^" + GlobToRegex(pattern) + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (pattern.Contains('/'))
            {
                _pathRules.Add(regex);
            }
            else
            {
                _nameRules.Add(regex);
            }
        }
    }

    /// <param name="relativePath">Path relative to the source folder being backed up.</param>
    public bool IsExcluded(string relativePath)
    {
        var rel = relativePath.Replace('\\', '/').Trim('/');
        var name = rel[(rel.LastIndexOf('/') + 1)..];

        foreach (var rule in _nameRules)
        {
            if (rule.IsMatch(name))
            {
                return true;
            }
        }

        foreach (var rule in _pathRules)
        {
            if (rule.IsMatch(rel))
            {
                return true;
            }
        }

        return false;
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        // "**/" = zero or more directories
                        i++;
                        sb.Append("(?:.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        return sb.ToString();
    }
}
