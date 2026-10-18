using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SqlMcp.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace SqlMcp.Tests;

public class HttpSecurityTests
{
    [Theory]
    [InlineData("http://localhost:8080", true)]
    [InlineData("http://127.0.0.1:8080", true)]
    [InlineData("http://127.10.0.1:8080", true)]
    [InlineData("http://[::1]:8080", true)]
    [InlineData("http://*:8080", false)]
    [InlineData("http://+:8080", false)]
    [InlineData("http://0.0.0.0:8080", false)]
    [InlineData("http://[::]:8080", false)]
    [InlineData("http://192.168.1.10:8080", false)]
    [InlineData("http://db-proxy.internal:8080", false)]
    [InlineData("not a url", false)]
    public void Classifies_loopback_urls(string url, bool loopback) =>
        Assert.Equal(loopback, HttpSecurity.IsLoopback(url));

    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    [Fact]
    public void Starts_unauthenticated_on_localhost() =>
        HttpSecurity.ValidateStartup(Config(("App:Urls", "http://localhost:8080")));

    [Fact]
    public void Defaults_to_localhost() =>
        HttpSecurity.ValidateStartup(Config());

    [Fact]
    public void Refuses_to_start_unauthenticated_on_all_interfaces()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            HttpSecurity.ValidateStartup(Config(("App:Urls", "http://localhost:8080;http://*:9090"))));
        Assert.Contains("http://*:9090", ex.Message);
    }

    [Fact]
    public void Starts_on_all_interfaces_with_agent_tokens_or_an_explicit_opt_out()
    {
        HttpSecurity.ValidateStartup(Config(("App:Urls", "http://*:8080"), ("App:AgentTokens:reporting", "secret")));
        HttpSecurity.ValidateStartup(Config(("App:Urls", "http://*:8080"), ("App:AllowUnauthenticated", "true")));
    }

    [Fact]
    public void Refuses_blank_and_shared_agent_tokens()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => HttpSecurity.ValidateStartup(Config(
            ("App:AgentTokens:blank", ""),
            ("App:AgentTokens:reporting", "same"),
            ("App:AgentTokens:billing", "same"),
            ("App:AgentTokens:support", "other"))));

        Assert.Contains("Agent 'blank' has no token", ex.Message);
        Assert.Contains("'reporting'", ex.Message);
        Assert.Contains("'billing'", ex.Message);
        Assert.Contains("share a token", ex.Message);
        Assert.DoesNotContain("'support'", ex.Message);
    }

    private const string ListDatabasesCall =
        """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_databases","arguments":{}}}""";

    private static HttpClient Client(string? token = null, string[]? allowedOrigins = null, LogCollector? logs = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            if (token is not null)
            {
                b.UseSetting("App:AgentTokens:reporting", token);
                b.UseSetting("App:AgentTokens:billing", token + "-billing");
            }
            for (int i = 0; i < (allowedOrigins?.Length ?? 0); i++)
                b.UseSetting($"App:AllowedOrigins:{i}", allowedOrigins![i]);
            if (logs is not null)
                b.ConfigureLogging(l => l.AddProvider(logs));
        });
        return factory.CreateClient();
    }

    private static HttpRequestMessage McpRequest(string? bearer = null, string? origin = null, string body = ListDatabasesCall)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        return request;
    }

    [Fact]
    public async Task Without_a_token_configured_mcp_is_open_to_local_callers()
    {
        var response = await Client().SendAsync(McpRequest());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task With_a_token_configured_mcp_requires_it()
    {
        var client = Client(token: "s3cret");

        var missing = await client.SendAsync(McpRequest());
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("Bearer", missing.Headers.WwwAuthenticate.ToString());

        var wrong = await client.SendAsync(McpRequest(bearer: "s3cre"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var right = await client.SendAsync(McpRequest(bearer: "s3cret"));
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.Contains("samplePostgres", await right.Content.ReadAsStringAsync());

        var otherAgent = await client.SendAsync(McpRequest(bearer: "s3cret-billing"));
        Assert.Equal(HttpStatusCode.OK, otherAgent.StatusCode);
    }

    private const string DeleteCall =
        """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"query_database","arguments":{"dbName":"samplePostgres","sql":"DELETE FROM orders"}}}""";

    [Fact]
    public async Task Logs_each_call_under_the_agent_whose_token_it_carries()
    {
        var logs = new LogCollector();
        var client = Client(token: "s3cret", logs: logs);

        await client.SendAsync(McpRequest(bearer: "s3cret", body: DeleteCall));
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/query")
        {
            Content = new StringContent("""{"dbName":"nope","sql":"SELECT 1"}""", Encoding.UTF8, "application/json"),
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", "s3cret-billing") }
        });

        var refused = Assert.Single(logs.Messages, m => m.Text.Contains("DELETE FROM orders"));
        Assert.Equal(LogLevel.Warning, refused.Level);
        Assert.Contains("Refused query: DELETE FROM orders on samplePostgres for reporting:", refused.Text);
        Assert.Contains(logs.Messages, m => m.Text.Contains("Refused query: SELECT 1 on nope for billing:"));
    }

    [Fact]
    public async Task Without_tokens_calls_are_logged_as_unauthenticated()
    {
        var logs = new LogCollector();

        await Client(logs: logs).SendAsync(McpRequest(body: DeleteCall));

        Assert.Contains(logs.Messages, m => m.Text.Contains("on samplePostgres for unauthenticated:"));
    }

    [Fact]
    public async Task Logs_requests_rejected_for_their_token()
    {
        var logs = new LogCollector();

        await Client(token: "s3cret", logs: logs).SendAsync(McpRequest(bearer: "guess"));

        var rejected = Assert.Single(logs.Messages, m => m.Text.StartsWith("Rejected POST /mcp"));
        Assert.Equal(LogLevel.Warning, rejected.Level);
        Assert.Contains("missing or unknown bearer token", rejected.Text);
        Assert.DoesNotContain(logs.Messages, m => m.Text.Contains("guess"));
    }

    [Fact]
    public async Task With_a_token_configured_the_rest_api_requires_it_but_health_does_not()
    {
        var client = Client(token: "s3cret");

        var query = await client.PostAsync("/api/query",
            new StringContent("""{"dbName":"x","sql":"SELECT 1"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, query.StatusCode);

        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Rejects_browser_origins_that_are_not_allowed()
    {
        var response = await Client().SendAsync(McpRequest(origin: "https://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Accepts_allowed_origins()
    {
        var response = await Client(allowedOrigins: ["https://app.example"]).SendAsync(McpRequest(origin: "https://app.example"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    internal sealed class LogCollector : ILoggerProvider, ILogger
    {
        public List<(LogLevel Level, string Text)> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
                Messages.Add((logLevel, formatter(state, exception)));
        }
        public void Dispose() { }
    }
}
