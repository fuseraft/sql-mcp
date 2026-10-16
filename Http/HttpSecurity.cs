using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SqlMcp.Http;

/// <summary>
/// Protects the HTTP transport. Requests to /mcp and /api/* must carry
/// "Authorization: Bearer {App:AuthToken}" when a token is configured, and any request
/// with an Origin header must come from App:AllowedOrigins (the MCP spec requires
/// Origin checks so a web page can't reach a local server through DNS rebinding).
/// Without a token the server refuses to listen beyond loopback unless
/// App:AllowUnauthenticated is set.
/// </summary>
public static class HttpSecurity
{
    public const string DefaultUrls = "http://localhost:8080";

    /// <summary>
    /// Throws when the server would accept unauthenticated requests from other machines.
    /// </summary>
    public static void ValidateStartup(IConfiguration config)
    {
        if (!string.IsNullOrEmpty(config["App:AuthToken"]) || config.GetValue("App:AllowUnauthenticated", false))
            return;

        var exposed = ParseUrls(config["App:Urls"] ?? DefaultUrls).Where(u => !IsLoopback(u)).ToList();
        if (exposed.Count > 0)
            throw new InvalidOperationException(
                $"Refusing to listen on {string.Join(", ", exposed)} without authentication. " +
                "Set App:AuthToken (env App__AuthToken) to require a bearer token, bind to localhost, " +
                "or set App:AllowUnauthenticated to true if something in front of this server authenticates.");
    }

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
        var token = app.Configuration["App:AuthToken"];
        var expected = string.IsNullOrEmpty(token) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(token));
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

            if (expected is not null && RequiresAuth(context.Request.Path) && !HasValidToken(context.Request, expected))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }

            await next();
        });
    }

    private static bool RequiresAuth(PathString path) =>
        !path.Equals("/health", StringComparison.OrdinalIgnoreCase);

    private static bool HasValidToken(HttpRequest request, byte[] expected)
    {
        var header = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        // Compare fixed-length hashes so neither content nor length leaks through timing.
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(header[prefix.Length..].Trim()));
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }
}
