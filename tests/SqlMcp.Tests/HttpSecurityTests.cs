using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
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
    public void Starts_on_all_interfaces_with_a_token_or_an_explicit_opt_out()
    {
        HttpSecurity.ValidateStartup(Config(("App:Urls", "http://*:8080"), ("App:AuthToken", "secret")));
        HttpSecurity.ValidateStartup(Config(("App:Urls", "http://*:8080"), ("App:AllowUnauthenticated", "true")));
    }

    private const string ListDatabasesCall =
        """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_databases","arguments":{}}}""";

    private static HttpClient Client(string? token = null, string[]? allowedOrigins = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("App:AuthToken", token ?? "");
            for (int i = 0; i < (allowedOrigins?.Length ?? 0); i++)
                b.UseSetting($"App:AllowedOrigins:{i}", allowedOrigins![i]);
        });
        return factory.CreateClient();
    }

    private static HttpRequestMessage McpRequest(string? bearer = null, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(ListDatabasesCall, Encoding.UTF8, "application/json")
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
}
