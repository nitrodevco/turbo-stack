using System.Text;

namespace TurboManager.Agent.Nginx;

/// <summary>
/// An nginx config can serve any file on disk or load code, so configs from the panel are checked
/// before they are written: file-path directives must stay under allowed roots, and module or
/// scripting directives are refused. Custom configs typed by an admin go through the same check.
/// </summary>
public static class NginxConfigPolicy
{
    public const int MaxLength = 256 * 1024;

    /// <summary>Directives whose first argument is a filesystem path.</summary>
    private static readonly Dictionary<string, string[]> PathDirectives = new(StringComparer.Ordinal)
    {
        ["root"] = WebRoots,
        ["alias"] = WebRoots,
        ["include"] = ["/etc/nginx/", "/etc/turbo-stack/nginx/"],
        ["ssl_certificate"] = ["/etc/turbo-stack/certs/", "/etc/turbo-stack/tls/", "/etc/ssl/"],
        ["ssl_certificate_key"] = ["/etc/turbo-stack/certs/", "/etc/turbo-stack/tls/", "/etc/ssl/private/ssl-cert-snakeoil.key"],
        ["ssl_trusted_certificate"] = ["/etc/turbo-stack/certs/", "/etc/ssl/"],
        ["ssl_client_certificate"] = ["/etc/turbo-stack/certs/", "/etc/ssl/"],
        ["ssl_dhparam"] = ["/etc/turbo-stack/", "/etc/ssl/"],
        ["access_log"] = ["/var/log/nginx/", "off"],
        ["error_log"] = ["/var/log/nginx/"],
        ["auth_basic_user_file"] = ["/etc/turbo-stack/nginx/"],
    };

    private static string[] WebRoots => ["/var/www/", "/srv/", "/home/", "/opt/turbo-stack/", "/var/lib/turbo-stack-acme", "/usr/share/nginx/html"];

    /// <summary>Directives that load code, run scripts or write files elsewhere.</summary>
    private static readonly string[] ForbiddenPrefixes =
    [
        "load_module", "perl", "perl_", "lua_", "content_by_lua", "access_by_lua", "rewrite_by_lua", "js_", "njs_",
        "client_body_temp_path", "proxy_temp_path", "fastcgi_temp_path", "uwsgi_temp_path", "scgi_temp_path",
        "proxy_cache_path", "fastcgi_cache_path", "uwsgi_cache_path", "scgi_cache_path", "proxy_store", "fastcgi_store",
        "dav_methods", "create_full_put_path", "user", "pid", "env", "daemon", "master_process", "working_directory",
        "ssl_engine", "debug_points", "lock_file", "worker_",
    ];

    public static IReadOnlyList<string> Check(string content)
    {
        var errors = new List<string>();

        if (content.Length > MaxLength)
        {
            errors.Add("The configuration is too large.");

            return errors;
        }

        if (content.Contains('\0'))
        {
            errors.Add("The configuration contains invalid characters.");

            return errors;
        }

        foreach (var (directive, arguments, line) in Tokenize(content))
        {
            if (ForbiddenPrefixes.Any(p => directive.Equals(p, StringComparison.Ordinal) || (p.EndsWith('_') && directive.StartsWith(p, StringComparison.Ordinal))))
            {
                errors.Add($"Line {line}: \"{directive}\" is not allowed in panel-managed configs.");

                continue;
            }

            if (!PathDirectives.TryGetValue(directive, out var allowed) || arguments.Count == 0)
            {
                continue;
            }

            var path = arguments[0];

            // Variables could point anywhere at runtime.
            if (path.Contains('$') && directive is not ("access_log" or "error_log"))
            {
                errors.Add($"Line {line}: \"{directive}\" can't use variables in its path.");

                continue;
            }

            // "/." also excludes hidden directories such as ~/.ssh under an allowed /home/ root.
            if (path.Contains("..", StringComparison.Ordinal) || path.Contains("/.", StringComparison.Ordinal) && !path.StartsWith("/var/lib/turbo-stack-acme", StringComparison.Ordinal)
                || !allowed.Any(a => path == a || path.StartsWith(a, StringComparison.Ordinal)))
            {
                errors.Add($"Line {line}: \"{directive} {path}\" is outside the allowed locations ({string.Join(", ", allowed)}).");
            }
        }

        return errors;
    }

    /// <summary>
    /// Splits a config into simple directives (name + arguments), honouring quotes, comments and
    /// blocks. Block openers like "location /x {" are reported as directives too.
    /// </summary>
    public static IEnumerable<(string Directive, List<string> Arguments, int Line)> Tokenize(string content)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var line = 1;
        var startLine = 1;
        char? quote = null;
        var escaped = false;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];

            if (c == '\n')
            {
                line++;
            }

            if (quote is not null)
            {
                if (escaped)
                {
                    current.Append(c);
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '#':
                    while (i + 1 < content.Length && content[i + 1] != '\n')
                    {
                        i++;
                    }

                    break;
                case '"' or '\'':
                    quote = c;

                    break;
                case ';' or '{' or '}':
                    Flush();

                    if (words.Count > 0)
                    {
                        yield return (words[0], words.Skip(1).ToList(), startLine);
                    }

                    words.Clear();

                    break;
                default:
                    if (char.IsWhiteSpace(c))
                    {
                        Flush();
                    }
                    else
                    {
                        if (words.Count == 0 && current.Length == 0)
                        {
                            startLine = line;
                        }

                        current.Append(c);
                    }

                    break;
            }
        }

        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
    }
}
