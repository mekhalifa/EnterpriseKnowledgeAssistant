using System.Net;
using System.Text.Json;
using Shouldly;

namespace EKA.IntegrationTests.Api;

/// <summary>
/// What a caller sees while the database is down: 503, not 500 (step-14 §6.7).
/// </summary>
/// <remarks>
/// Through the real pipeline: a valid token, authorization, the handler and repository, and
/// the exception handler chain. The point is the mapping at the edge, which only a request
/// that actually travels the pipeline can show; the classification itself is tested against
/// real exceptions in <c>SqlServerFailuresTests</c>.
/// </remarks>
public sealed class DatabaseOutageTests : IDisposable
{
    // A port with nothing behind it. Connect Timeout keeps the test short; the shape of the
    // failure is the same as with the default 15 s.
    private readonly EkaApiFactory _factory = new(
        "Server=127.0.0.1,1;Database=nothing;User Id=sa;Password=nope;TrustServerCertificate=True;Connect Timeout=2");

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData("/api/documents")]
    [InlineData("/api/conversations")]
    public async Task A_read_while_the_database_is_unreachable_is_a_retryable_503(string path)
    {
        var response = await _factory.CreateClientFor(Guid.NewGuid()).GetAsync(path);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        problem.GetProperty("code").GetString().ShouldBe("database.unavailable");

        // The trace id, so the person who reports it can be matched to the log line.
        problem.GetProperty("traceId").GetString().ShouldNotBeNullOrWhiteSpace();

        // The connection error names a host and port; that belongs in the log, not the response.
        var body = problem.ToString();
        body.ShouldNotContain("127.0.0.1");
        body.ShouldNotContain("SqlException");
    }
}
