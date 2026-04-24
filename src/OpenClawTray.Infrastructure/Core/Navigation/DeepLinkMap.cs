using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenClawTray.Infrastructure.Navigation;

/// <summary>
/// Provides typed access to parameters extracted from a URI pattern match.
/// </summary>
public sealed class RouteArgs
{
    private readonly Dictionary<string, string> _values;
    private readonly Dictionary<string, string>? _queryParams;

    internal RouteArgs(Dictionary<string, string> values, Dictionary<string, string>? queryParams = null)
    {
        _values = values;
        _queryParams = queryParams;
    }

    /// <summary>
    /// Gets a parameter value by name, converted to the specified type.
    /// Supports <c>int</c>, <c>long</c>, <c>bool</c>, <c>Guid</c>, and <c>string</c>.
    /// </summary>
    public T Get<T>(string name)
    {
        if (!_values.TryGetValue(name, out var raw))
            throw new KeyNotFoundException($"Parameter '{name}' not found in route args.");

        return ConvertValue<T>(name, raw);
    }

    /// <summary>
    /// Gets an optional parameter value. Returns <paramref name="defaultValue"/>
    /// if the parameter was not present in the matched URI.
    /// </summary>
    public T GetOrDefault<T>(string name, T defaultValue = default!)
    {
        if (_values.TryGetValue(name, out var raw) && !string.IsNullOrEmpty(raw))
            return ConvertValue<T>(name, raw);
        return defaultValue;
    }

    /// <summary>
    /// Returns the raw string value of a parameter, or null if not found.
    /// </summary>
    public string? GetString(string name) =>
        _values.TryGetValue(name, out var v) ? v : null;

    /// <summary>
    /// Gets a query string parameter value by name, converted to the specified type.
    /// Returns <paramref name="defaultValue"/> if the parameter is not present.
    /// </summary>
    public T Query<T>(string name, T defaultValue = default!)
    {
        if (_queryParams is null || !_queryParams.TryGetValue(name, out var raw))
            return defaultValue;
        return ConvertValue<T>(name, raw);
    }

    /// <summary>
    /// Returns the raw query string parameter value, or null if not found.
    /// </summary>
    public string? QueryString(string name) =>
        _queryParams?.TryGetValue(name, out var v) == true ? v : null;

    /// <summary>
    /// Gets a wildcard/catch-all segment value captured by a <c>**</c> pattern.
    /// </summary>
    public string? GetWildcard() => GetString("**");

    private static T ConvertValue<T>(string name, string raw)
    {
        var type = typeof(T);
        object result;
        try
        {
            result = type switch
            {
                _ when type == typeof(string) => raw,
                _ when type == typeof(int) => int.Parse(raw, CultureInfo.InvariantCulture),
                _ when type == typeof(long) => long.Parse(raw, CultureInfo.InvariantCulture),
                _ when type == typeof(bool) => bool.Parse(raw),
                _ when type == typeof(Guid) => Guid.Parse(raw),
                _ => throw new NotSupportedException($"RouteArgs.Get<{type.Name}> is not supported."),
            };
        }
        catch (FormatException ex)
        {
            throw new FormatException($"Route parameter '{name}' value '{raw}' is not a valid {type.Name}.", ex);
        }
        return (T)result;
    }
}

/// <summary>
/// Result of a deep link resolution attempt.
/// </summary>
public readonly struct DeepLinkResult<TRoute>
{
    /// <summary>The matched routes (current + optional synthetic back stack).</summary>
    public TRoute[] Routes { get; init; }

    /// <summary>True if the URI matched a registered pattern.</summary>
    public bool Matched { get; init; }
}

/// <summary>
/// Maps URI patterns to route constructors for deep linking.
/// Patterns use <c>/segment/{param:type}</c> syntax where type is <c>int</c> or <c>string</c>.
/// </summary>
/// <summary>
/// Maps URI patterns to route constructors for deep linking.
/// Supports <c>/segment/{param:type}</c>, optional params <c>{param?}</c>,
/// wildcards <c>/path/**</c>, and query string parsing.
/// </summary>
public sealed class DeepLinkMap<TRoute> where TRoute : notnull
{
    private readonly List<(Regex Pattern, string[] ParamNames, Func<RouteArgs, TRoute> Factory, Func<TRoute[]>? BackStackFactory)> _routes = new();

    public DeepLinkMap<TRoute> Map(string pattern, Func<RouteArgs, TRoute> factory)
    {
        var (regex, paramNames) = CompilePattern(pattern);
        _routes.Add((regex, paramNames, factory, null));
        return this;
    }

    public DeepLinkMap<TRoute> Map(string pattern, Func<RouteArgs, TRoute> factory, Func<TRoute[]> backStackFactory)
    {
        var (regex, paramNames) = CompilePattern(pattern);
        _routes.Add((regex, paramNames, factory, backStackFactory));
        return this;
    }

    public DeepLinkResult<TRoute> Resolve(Uri uri)
    {
        var queryParams = ParseQueryString(uri.Query);
        return Resolve(uri.AbsolutePath, queryParams);
    }

    public DeepLinkResult<TRoute> Resolve(string path)
    {
        Dictionary<string, string>? queryParams = null;
        var qi = path.IndexOf('?');
        if (qi >= 0)
        {
            queryParams = ParseQueryString(path.Substring(qi));
            path = path.Substring(0, qi);
        }
        return Resolve(path, queryParams);
    }

    private DeepLinkResult<TRoute> Resolve(string path, Dictionary<string, string>? queryParams)
    {
        path = path.TrimEnd('/');
        if (string.IsNullOrEmpty(path))
            path = "/";

        foreach (var (pattern, paramNames, factory, backStackFactory) in _routes)
        {
            var match = pattern.Match(path);
            if (!match.Success)
                continue;

            var values = new Dictionary<string, string>();
            for (int i = 0; i < paramNames.Length; i++)
            {
                var group = match.Groups[i + 1];
                if (group.Success && group.Length > 0)
                    values[paramNames[i]] = group.Value;
            }

            var route = factory(new RouteArgs(values, queryParams));

            if (backStackFactory is not null)
            {
                var backStack = backStackFactory();
                var all = new TRoute[backStack.Length + 1];
                Array.Copy(backStack, all, backStack.Length);
                all[^1] = route;
                return new DeepLinkResult<TRoute> { Routes = all, Matched = true };
            }

            return new DeepLinkResult<TRoute> { Routes = new[] { route }, Matched = true };
        }

        return new DeepLinkResult<TRoute> { Routes = Array.Empty<TRoute>(), Matched = false };
    }

    private static Dictionary<string, string>? ParseQueryString(string? query)
    {
        if (string.IsNullOrEmpty(query)) return null;
        if (query.StartsWith('?')) query = query.Substring(1);
        if (string.IsNullOrEmpty(query)) return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq >= 0)
                result[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1));
            else
                result[Uri.UnescapeDataString(pair)] = "";
        }
        return result;
    }

    private static (Regex Regex, string[] ParamNames) CompilePattern(string pattern)
    {
        var paramNames = new List<string>();

        // Handle wildcard: /path/** → /path/(.+)
        string regexPattern;
        if (pattern.EndsWith("/**"))
        {
            var prefix = pattern.Substring(0, pattern.Length - 3);
            prefix = CompileSegments(prefix, paramNames);
            paramNames.Add("**");
            regexPattern = $"{prefix}/(.+)";
        }
        else
        {
            regexPattern = CompileSegments(pattern, paramNames);
        }

        return (new Regex($"^{regexPattern}$", RegexOptions.Compiled | RegexOptions.IgnoreCase), paramNames.ToArray());
    }

    private static string CompileSegments(string pattern, List<string> paramNames)
    {
        // First: replace optional /{name?} or /{name:type?} — preceding / is part of the optional group
        var result = Regex.Replace(pattern, @"/\{(\w+)(?::(\w+))?\?\}", m =>
        {
            var name = m.Groups[1].Value;
            var type = m.Groups[2].Success ? m.Groups[2].Value : "string";
            paramNames.Add(name);
            return $"(?:/({GetTypePattern(type)}))?";
        });

        // Then: replace required {name} or {name:type}
        result = Regex.Replace(result, @"\{(\w+)(?::(\w+))?\}", m =>
        {
            var name = m.Groups[1].Value;
            var type = m.Groups[2].Success ? m.Groups[2].Value : "string";
            paramNames.Add(name);
            return $"({GetTypePattern(type)})";
        });

        return result;
    }

    private static string GetTypePattern(string type) => type switch
    {
        "int" => @"\d+",
        "long" => @"\d+",
        "bool" => @"true|false",
        "guid" => @"[0-9a-fA-F\-]+",
        _ => @"[^/]+",
    };
}
