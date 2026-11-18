using System.ComponentModel;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Storage;

namespace EKA.Infrastructure.Persistence;

/// <summary>
/// Tells "the database could not be reached" apart from every other database failure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> The host maps an unreachable database to <c>503</c>, which tells
/// clients and on-call people the problem is temporary and the request may be retried. Any
/// other database exception (a bad query, a failed login, a constraint nobody anticipated)
/// stays a <c>500</c>, which says the problem is ours. The rule lives here because this is the
/// only assembly that knows the store is SQL Server; the API asks a question, not "which
/// <c>SqlException</c> numbers".
/// </para>
/// <para>
/// <b>Why not a list of error numbers.</b> It is the usual approach, and it fails on Linux, where
/// the deployment runs. Measured with SqlClient 6.1.7 (docs/steps/post-01, ADR-0037):
/// a refused connection is number <c>1225</c> on Windows and <c>0</c> on Linux, an unresolvable host
/// is <c>11002</c>, a connect timeout is <c>258</c>, and <see cref="SqlException.IsTransient"/> is false
/// for all four. What they share is structural: the exception was caused by an
/// operating-system network error. That is what "unreachable" means, so that is the test.
/// </para>
/// </remarks>
public static class SqlServerFailures
{
    /// <summary>
    /// True when <paramref name="exception"/>, or anything it wraps, says the database could
    /// not be reached or is temporarily unable to serve.
    /// </summary>
    public static bool IsUnavailable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                // EF's execution strategy retried a failure it classifies as transient and ran
                // out of attempts: unavailable by definition.
                case RetryLimitExceededException:
                    return true;

                // The connection could not be established: the network layer failed underneath.
                // Win32Exception covers Windows' native network layer and connect timeouts on
                // both platforms; SocketException covers Linux' managed one.
                case SqlException { InnerException: SocketException or Win32Exception }:
                    return true;

                // Errors SqlClient itself flags as transient: server-side "busy, try again"
                // conditions such as an Azure SQL failover or throttling.
                case SqlException { IsTransient: true }:
                    return true;
            }
        }

        return false;
    }
}
