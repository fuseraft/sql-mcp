using System.Text.RegularExpressions;

namespace SqlMcp.Http;

/// <summary>
/// "sql-mcp --healthcheck" asks the running server's /health endpoint and exits 0 if it
/// answers 200, else 1. The container image uses it as its HEALTHCHECK, since the
/// ASP.NET base image has no curl or wget.
/// </summary>
public static partial class HealthCheck
{
    public static async Task<int> RunAsync(IConfiguration config)
    {
        var url = LocalUrl(config["App:Urls"] ?? HttpSecurity.DefaultUrls);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(new Uri(new Uri(url), "/health"));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            Console.Error.WriteLine($"Health check of {url} failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// The first bound URL, with a wildcard host ("*", "+", "0.0.0.0", "[::]") replaced by
    /// localhost so it can be dialled.
    /// </summary>
    public static string LocalUrl(string urls)
    {
        var url = HttpSecurity.ParseUrls(urls).First().TrimEnd('/');
        var m = HostPattern().Match(url);
        return m.Success && m.Groups["host"].Value is "*" or "+" or "0.0.0.0" or "[::]"
            ? m.Groups["scheme"].Value + "localhost" + m.Groups["rest"].Value
            : url;
    }

    [GeneratedRegex(@"^(?<scheme>[a-z]+://)(?<host>\[[^\]]*\]|[^:/]+)(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex HostPattern();
}
