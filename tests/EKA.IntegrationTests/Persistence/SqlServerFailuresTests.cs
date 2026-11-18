using EKA.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Storage;
using Shouldly;

namespace EKA.IntegrationTests.Persistence;

/// <summary>
/// Which database failures count as "unavailable" (503) and which stay ours (500).
/// </summary>
/// <remarks>
/// Every <see cref="SqlException"/> here is real, produced by a real failed connection or
/// command. The type has no public constructor, and a hand-built stand-in would only prove the
/// classifier matches the shape the test author imagined. The real shapes differ by platform:
/// a refused connection is number 1225 on Windows and 0 on Linux, which is why the rule is
/// structural. CI runs these on Linux, a developer on Windows; both must pass.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class SqlServerFailuresTests(SqlServerFixture fixture)
{
    // No pooling, so each case meets its own failure rather than one the pool cached for a
    // few seconds after an earlier test's failed connection (SqlClient's blocking period).
    private const string Common = "Database=nothing;User Id=sa;Password=nope;TrustServerCertificate=True;Pooling=false;Connect Timeout=2";

    private static async Task<Exception> FailureOpeningAsync(string connectionString, string? command = null)
    {
        await using var connection = new SqlConnection(connectionString);

        return await Should.ThrowAsync<Exception>(async () =>
        {
            await connection.OpenAsync();
            if (command is not null)
            {
                await using var sql = connection.CreateCommand();
                sql.CommandText = command;
                await sql.ExecuteNonQueryAsync();
            }
        });
    }

    [Fact]
    public async Task A_refused_connection_is_unavailable()
    {
        // A port with nothing listening: what a stopped SQL Server container looks like.
        var failure = await FailureOpeningAsync($"Server=127.0.0.1,1;{Common}");

        failure.ShouldBeOfType<SqlException>();
        SqlServerFailures.IsUnavailable(failure).ShouldBeTrue();
    }

    [Fact]
    public async Task A_host_that_does_not_resolve_is_unavailable()
    {
        // A stopped Compose service: its name leaves the network's DNS. ".invalid" is reserved
        // and never resolves (RFC 6761).
        var failure = await FailureOpeningAsync($"Server=sql.eka.invalid,1433;{Common}");

        SqlServerFailures.IsUnavailable(failure).ShouldBeTrue();
    }

    [Fact]
    public async Task Exhausted_transient_retries_are_unavailable()
    {
        // What EF's execution strategy throws after its last retry, wrapping the last failure.
        var last = await FailureOpeningAsync($"Server=127.0.0.1,1;{Common}");

        SqlServerFailures.IsUnavailable(new RetryLimitExceededException("Retries exhausted.", last)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_rejected_login_is_not_unavailable_because_retrying_cannot_fix_it()
    {
        // The server answered. A wrong password is a configuration fault: ours, a 500.
        var builder = new SqlConnectionStringBuilder(fixture.ConnectionString) { Password = "wrong-password", Pooling = false };

        var failure = await FailureOpeningAsync(builder.ConnectionString);

        failure.ShouldBeOfType<SqlException>();
        SqlServerFailures.IsUnavailable(failure).ShouldBeFalse();
    }

    [Fact]
    public async Task A_failing_query_on_a_reachable_server_is_not_unavailable()
    {
        // A bug in our SQL must stay a 500. Mapping it to 503 would invite clients to retry a
        // request that fails every time, and hide the bug behind a "temporary" label.
        var failure = await FailureOpeningAsync(fixture.ConnectionString, "SELECT * FROM dbo.NoSuchTable");

        failure.ShouldBeOfType<SqlException>();
        SqlServerFailures.IsUnavailable(failure).ShouldBeFalse();
    }

    [Fact]
    public void An_exception_from_anywhere_else_is_not_unavailable() =>
        SqlServerFailures.IsUnavailable(new InvalidOperationException("Not a database failure.")).ShouldBeFalse();
}
