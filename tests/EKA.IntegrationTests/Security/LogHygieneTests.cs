using System.Collections.Concurrent;
using System.Net.Http.Json;
using EKA.Contracts.Conversations;
using EKA.Contracts.Retrieval;
using EKA.IntegrationTests.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace EKA.IntegrationTests.Security;

/// <summary>
/// Questions, answers and document text never reach the logs, at any level down to Debug
/// (Step 12).
/// </summary>
/// <remarks>
/// Logs are copied to places with weaker access control than the database: log aggregators,
/// support tickets, a developer's terminal. A question like "what is my colleague's salary band"
/// is as sensitive as the answer. The test captures every log line the real pipeline writes
/// during an upload-free ask and search, and checks that none carries the user's words or the
/// document's.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class LogHygieneTests(SqlServerFixture sqlServer)
{
    private const string QuestionMarker = "QUESTION-MARKER-91C2";
    private const string DocumentMarker = "DOCUMENT-MARKER-4E7B";

    [Fact]
    public async Task No_log_line_carries_the_question_the_answer_or_the_document_text()
    {
        var captured = new ConcurrentQueue<string>();
        using var api = new EkaApiFactory(
            sqlServer.ConnectionString,
            new Dictionary<string, string?> { ["VectorStore:CollectionName"] = $"logs_{Guid.NewGuid():N}" },
            sqlServer.QdrantEndpoint,
            configureServices: services => services.AddLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddProvider(new CapturingProvider(captured));
            }));
        var tenant = Guid.NewGuid();
        await SearchableDocuments.SeedAsync(sqlServer, api, tenant, $"Annual leave is 25 days. {DocumentMarker}");
        api.Chat.Reply = $"You get 25 days [1]. {DocumentMarker}";
        var client = api.CreateClientFor(tenant, "alice");

        (await client.PostAsJsonAsync("/api/search", new SearchRequest($"leave {QuestionMarker}"))).EnsureSuccessStatusCode();
        var conversation = (await (await client.PostAsync("/api/conversations", content: null)).Content.ReadFromJsonAsync<ConversationResponse>())!;
        (await client.PostAsJsonAsync($"/api/conversations/{conversation.Id}/messages", new AskQuestionRequest($"How much leave? {QuestionMarker}")))
            .EnsureSuccessStatusCode();

        // The capture must have seen the application's own lines about this very request, or
        // a passing assertion below would prove only that nothing was captured.
        captured.ShouldContain(line => line.Contains("Retrieval for tenant", StringComparison.Ordinal));
        captured.ShouldAllBe(line => !line.Contains(QuestionMarker) && !line.Contains(DocumentMarker));
    }

    private sealed class CapturingProvider(ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, lines);

        public void Dispose()
        {
        }

        private sealed class Capturing(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
        }
    }
}
