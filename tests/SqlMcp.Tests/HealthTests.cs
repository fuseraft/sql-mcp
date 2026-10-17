using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SqlMcp.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SqlMcp.Tests;

public class HealthTests
{
    [Theory]
    [InlineData("http://*:8080", "http://localhost:8080")]
    [InlineData("http://+:8080", "http://localhost:8080")]
    [InlineData("http://0.0.0.0:9000/", "http://localhost:9000")]
    [InlineData("http://[::]:8080", "http://localhost:8080")]
    [InlineData("http://127.0.0.1:8080;http://*:9090", "http://127.0.0.1:8080")]
    [InlineData("http://[::1]:8080", "http://[::1]:8080")]
    [InlineData("http://localhost", "http://localhost")]
    public void Dials_the_first_url_on_localhost(string urls, string expected) =>
        Assert.Equal(expected, HealthCheck.LocalUrl(urls));

    private static HttpClient Client(string? token = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("App:AuthToken", token ?? "")).CreateClient();

    // The sample config's databases have no passwords set, so none are ready.
    [Fact]
    public async Task Readiness_reports_each_database()
    {
        using var client = Client();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("unavailable", body.GetProperty("status").GetString());
        Assert.Contains("DB_SAMPLEPG_PASSWORD", body.GetProperty("databases").GetProperty("samplePostgres").GetString());
    }

    [Fact]
    public async Task Readiness_requires_the_token_but_liveness_does_not()
    {
        using var client = Client("secret");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Get, "/health/ready");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.SendAsync(request)).StatusCode);
    }
}
