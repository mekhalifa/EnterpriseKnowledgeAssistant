using EKA.Domain.Common;
using EKA.Domain.Conversations;
using EKA.Domain.Ingestion;
using EKA.Domain.Retrieval;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace EKA.IntegrationTests.Persistence;

/// <summary>
/// Writes aggregates and reads them back in a fresh context, proving the value-object
/// mappings survive a real round trip rather than merely compiling.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class RoundTripTests
{
    private readonly SqlServerFixture _fixture;

    public RoundTripTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task A_document_round_trips_with_every_value_object_intact()
    {
        var tenant = TestFixtures.NewTenant();
        var document = TestFixtures.CompletedDocument(tenant, chunkCount: 3);

        await using (var write = _fixture.CreateContext())
        {
            write.Documents.Add(document);
            await write.SaveChangesAsync();
        }

        // A second context with its own change tracker: otherwise the "loaded" entity
        // could just be the in-memory instance we wrote, and the test would prove nothing
        // about what reached the database.
        await using var read = _fixture.CreateContext();
        var loaded = await read.Documents
            .Include(d => d.Chunks)
            .FirstOrDefaultAsync(d => d.Id == document.Id);

        loaded.ShouldNotBeNull();
        loaded.TenantId.ShouldBe(tenant);
        loaded.Title.Value.ShouldBe("Employee Handbook");
        loaded.Status.ShouldBe(IngestionStatus.Completed);
        loaded.Authority.ShouldBe(AuthorityScore.Standard);
        loaded.ChunkCount.ShouldBe(3);

        // The owned FileMetadata became four columns on the same row.
        loaded.File.FileName.ShouldBe("handbook.pdf");
        loaded.File.ContentType.ShouldBe("application/pdf");
        loaded.File.SizeBytes.ShouldBe(2048);
        loaded.File.ContentHash.ShouldBe(TestFixtures.Sha256);

        loaded.Chunks.Count.ShouldBe(3);
        loaded.Chunks.Select(c => c.Index.Value).Order().ShouldBe([0, 1, 2]);

        var first = loaded.Chunks.Single(c => c.Index.Value == 0);
        first.TokenCount.Value.ShouldBe(120);
        first.SectionPath.ShouldBe("Benefits / Leave");
        first.PageRange.ShouldNotBeNull();
        first.PageRange.Start.ShouldBe(1);
        first.PageRange.End.ShouldBe(2);
    }

    [Fact]
    public async Task A_chunk_without_a_page_range_round_trips_as_null()
    {
        // Optional owned type: both columns are nullable and EF uses them to decide
        // whether the value object exists at all.
        var tenant = TestFixtures.NewTenant();
        var document = TestFixtures.NewDocument(tenant);

        var chunk = new DocumentChunk(
            Guid.NewGuid(),
            document.Id,
            ChunkIndex.First,
            "Plain text has no pages.",
            new TokenCount(10),
            TestFixtures.Now);

        await using (var write = _fixture.CreateContext())
        {
            write.Documents.Add(document);
            write.DocumentChunks.Add(chunk);
            await write.SaveChangesAsync();
        }

        await using var read = _fixture.CreateContext();
        var loaded = await read.DocumentChunks.FirstAsync(c => c.Id == chunk.Id);

        loaded.PageRange.ShouldBeNull();
        loaded.SectionPath.ShouldBeNull();
    }

    [Fact]
    public async Task A_conversation_round_trips_with_messages_and_citations()
    {
        var tenant = TestFixtures.NewTenant();
        var userId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        var conversation = new Conversation(Guid.NewGuid(), tenant, userId, TestFixtures.Now);
        conversation.AddUserMessage(
            Message.FromUser(Guid.NewGuid(), conversation.Id, "What is the leave policy?", TestFixtures.Now),
            TestFixtures.Now);

        conversation.AddAssistantMessage(
            Message.FromAssistant(
                Guid.NewGuid(),
                conversation.Id,
                "Employees receive 25 days.",
                providerName: "azure-openai",
                modelName: "gpt-5",
                TestFixtures.Now,
                citations:
                [
                    new Citation(
                        documentId,
                        "Employee Handbook",
                        new ChunkIndex(4),
                        new SimilarityScore(0.91),
                        new PageRange(12, 13),
                        "Benefits / Leave"),
                ],
                tokenCount: new TokenCount(240)),
            TestFixtures.Now);

        await using (var write = _fixture.CreateContext())
        {
            write.Conversations.Add(conversation);
            await write.SaveChangesAsync();
        }

        await using var read = _fixture.CreateContext();
        var loaded = await read.Conversations
            .Include(c => c.Messages)
            .ThenInclude(m => m.Citations)
            .FirstOrDefaultAsync(c => c.Id == conversation.Id);

        loaded.ShouldNotBeNull();
        loaded.Title.ShouldBe("What is the leave policy?");
        loaded.Messages.Count.ShouldBe(2);

        var answer = loaded.Messages.Single(m => m.Role == MessageRole.Assistant);
        answer.ProviderName.ShouldBe("azure-openai");
        answer.ModelName.ShouldBe("gpt-5");
        answer.TokenCount.ShouldNotBeNull();
        answer.TokenCount.Value.Value.ShouldBe(240);
        answer.IsUngrounded.ShouldBeFalse();

        var citation = answer.Citations.ShouldHaveSingleItem();
        citation.DocumentId.ShouldBe(documentId);
        citation.DocumentTitle.ShouldBe("Employee Handbook");
        citation.ChunkIndex.Value.ShouldBe(4);
        citation.Score.Value.ShouldBe(0.91, tolerance: 1e-9);
        citation.PageRange!.Start.ShouldBe(12);
        citation.SectionPath.ShouldBe("Benefits / Leave");
    }

    [Fact]
    public async Task A_users_roles_round_trip_through_the_primitive_collection()
    {
        var tenant = TestFixtures.NewTenant();
        var user = new EKA.Domain.Identity.User(
            Guid.NewGuid(),
            tenant,
            externalId: $"sub-{Guid.NewGuid():N}",
            email: "Amina.Yusuf@Example.COM",
            displayName: "Amina Yusuf",
            TestFixtures.Now);

        user.GrantRole("HR.Reader", TestFixtures.Now);
        user.GrantRole("Finance.Reader", TestFixtures.Now);

        await using (var write = _fixture.CreateContext())
        {
            write.Users.Add(user);
            await write.SaveChangesAsync();
        }

        await using var read = _fixture.CreateContext();
        var loaded = await read.Users.FirstAsync(u => u.Id == user.Id);

        loaded.Email.ShouldBe("amina.yusuf@example.com");
        loaded.Roles.Count.ShouldBe(2);
        loaded.IsInRole("hr.reader").ShouldBeTrue();
    }

    [Fact]
    public async Task Loading_without_Include_leaves_chunks_unloaded()
    {
        // GetByIdAsync and GetWithChunksAsync are separate methods for a reason: a
        // 300-page document has thousands of chunks, and loading them by accident is a
        // performance incident rather than a convenience.
        var tenant = TestFixtures.NewTenant();
        var document = TestFixtures.CompletedDocument(tenant, chunkCount: 5);

        await using (var write = _fixture.CreateContext())
        {
            write.Documents.Add(document);
            await write.SaveChangesAsync();
        }

        await using var read = _fixture.CreateContext();
        var loaded = await read.Documents.FirstAsync(d => d.Id == document.Id);

        loaded.Chunks.ShouldBeEmpty();
        // ChunkCount is a column, so the count is still available without the collection.
        loaded.ChunkCount.ShouldBe(5);
    }
}
