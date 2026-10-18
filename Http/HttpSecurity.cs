using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace SqlMcp.Http;

/// <summary>
/// Protects the HTTP transport. When App:AgentTokens maps agent names to tokens, requests
/// to /mcp and /api/* must carry "Authorization: Bearer {token}" for one of them, and the
/// request's user is that agent, so each call is logged under its name. Any request with
/// an Origin header must come from App:AllowedOrigins (the MCP spec requires Origin
/// checks so a web page can't reach a local server through DNS rebinding). Without
/// tokens the server refuses to listen beyond loopback unless App:AllowUnauthenticated
/// is set.
/// </summary>
public static class HttpSecurity
{
    public const string DefaultUrls = "http://localhost:8080";

    /// <summary>
    /// Throws, listing every problem, when an agent token is blank or shared, or when the
    /// server would accept unauthenticated requests from other machines.
    /// </summary>
    public static void ValidateStartup(IConfiguration config)
    {
        var errors = new List<string>();
        var agents = ReadAgentTokens(config);
        foreach (var (name, token) in agents.Where(a => string.IsNullOrWhiteSpace(a.Token)))
            errors.Add($"Agent '{name}' has no token in App:AgentTokens.");
        foreach (var shared in agents.Where(a => !string.IsNullOrWhiteSpace(a.Token)).GroupBy(a => a.Token).Where(g => g.Count() > 1))
            errors.Add($"Agents {string.Join(", ", shared.Select(a => $"'{a.Name}'"))} share a token, so their calls can't be told apart.");

        if (agents.Count == 0 && !config.GetValue("App:AllowUnauthenticated", false))
        {
            var exposed = ParseUrls(config["App:Urls"] ?? DefaultUrls).Where(u => !IsLoopback(u)).ToList();
            if (exposed.Count > 0)
                errors.Add($"Refusing to listen on {string.Join(", ", exposed)} without authentication. " +
                           "Give each agent a token under App:AgentTokens (env App__AgentTokens__<name>), bind to localhost, " +
                           "or set App:AllowUnauthenticated to true if something in front of this server authenticates.");
        }

        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid configuration:" + string.Concat(errors.Select(e => $"{Environment.NewLine}  - {e}")));
    }

    /// <summary>App:AgentTokens as (agent name, token) pairs.</summary>
    public static List<(string Name, string? Token)> ReadAgentTokens(IConfiguration config) =>
        [.. config.GetSection("App:AgentTokens").GetChildren().Select(c => (c.Key, c.Value))];

    public static IEnumerable<string> ParseUrls(string urls) =>
        urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>True only for URLs that bind to the loopback interface.</summary>
    public static bool IsLoopback(string url)
    {
        // Wildcards ("http://*:8080", "http://+:8080") aren't valid URIs; they bind everywhere.
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        var rest = scheme >= 0 ? url[(scheme + 3)..] : url;
        if (rest.StartsWith('*') || rest.StartsWith('+'))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.IsLoopback)
            return true; // localhost, 127.0.0.0/8, [::1]

        return IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }

    public static void UseHttpSecurity(this WebApplication app)
    {
        var agents = ReadAgentTokens(app.Configuration)
            .Select(a => (a.Name, Hash: SHA256.HashData(Encoding.UTF8.GetBytes(a.Token!))))
            .ToList();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(HttpSecurity));
        var allowedOrigins = new HashSet<string>(
            app.Configuration.GetSection("App:AllowedOrigins").Get<string[]>() ?? [],
            StringComparer.OrdinalIgnoreCase);

        app.Use(async (context, next) =>
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && !allowedOrigins.Contains(origin.TrimEnd('/')))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Origin not allowed.");
                return;
            }

            if (agents.Count > 0 && RequiresAuth(context.Request.Path))
            {
                if (FindAgent(context.Request, agents) is not { } agent)
                {
                    logger.LogWarning("Rejected {Method} {Path} from {RemoteIp}: missing or unknown bearer token.",
                        context.Request.Method, context.Request.Path, context.Connection.RemoteIpAddress);
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    return;
                }
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, agent)], "Bearer"));
            }

            await next();
        });
    }

    private static bool RequiresAuth(PathString path) =>
        !path.Equals("/health", StringComparison.OrdinalIgnoreCase);

    /// <summary>The agent whose token the request carries, or null.</summary>
    private static string? FindAgent(HttpRequest request, List<(string Name, byte[] Hash)> agents)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        // Compare fixed-length hashes so neither content nor length leaks through timing,
        // and check every agent so the time taken doesn't say which one matched.
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(header[prefix.Length..].Trim()));
        string? match = null;
        foreach (var (name, hash) in agents)
            if (CryptographicOperations.FixedTimeEquals(presented, hash))
                match = name;
        return match;
    }
}
