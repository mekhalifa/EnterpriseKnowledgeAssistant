using EKA.Domain.Ingestion;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace EKA.IntegrationTests.Persistence;

/// <summary>
/// Covers the two cross-cutting behaviours attached to <c>SaveChangesAsync</c>: audit
/// stamping and post-commit domain event dispatch.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class AuditAndEventTests
{
    private readonly SqlServerFixture _fixture;

    public AuditAndEventTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    // ── Audit stamping ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Inserting_stamps_CreatedBy_from_the_current_user()
    {
        var tenant = TestFixtures.NewTenant();
        var subject = $"sub-{Guid.NewGuid():N}";
        var document = TestFixtures.NewDocument(tenant);

        await using (var write = _fixture.CreateContext(new TestCurrentUser(subject)))
        {
            write.Documents.Add(document);
            await write.SaveChangesAsync();
        }

        await using var read = _fixture.CreateContext();
        var loaded = await read.Documents.FirstAsync(d => d.Id == document.Id);

        loaded.CreatedBy.ShouldBe(subject);
        loaded.ModifiedBy.ShouldBeNull();
    }

    [Fact]
    public async Task Updating_stamps_ModifiedBy_but_never_rewrites_CreatedBy()
    {
        // An audit record that can be rewritten is not an audit record.
        var tenant = TestFixtures.NewTenant();
        var creator = $"creator-{Guid.NewGuid():N}";
        var editor = $"editor-{Guid.NewGuid():N}";
        var document = TestFixtures.NewDocument(tenant);

        await using (var write = _fixture.CreateContext(new TestCurrentUser(creator)))
        {
            write.Documents.Add(document);
            await write.SaveChangesAsync();
        }

        await using (var update = _fixture.CreateContext(new TestCurrentUser(editor)))
        {
            var loaded = await update.Documents.FirstAsync(d => d.Id == document.Id);
            loaded.BeginExtraction(TestFixtures.Now.AddMinutes(1));
            await update.SaveChangesAsync();
        }

        await using var read = _fixture.CreateContext();
        var final = await read.Documents.FirstAsync(d => d.Id == document.Id);

        final.CreatedBy.ShouldBe(creator);
        final.ModifiedBy.ShouldBe(editor);
    }

    [Fact]
    public async Task Unauthenticated_writes_are_attributed_to_system()
    {
        // Background jobs and migrations have no interactive user. Recording "system" is
        // honest; leaving the column null would make it ambiguous whether attribution was
        // missing or simply never implemented.
        var tenant = TestFixtures.NewTenant();
        var document = TestFixtures.NewDocument(tenant);

        await using (var write = _fixture.CreateContext(new TestCurrentUser(null)))
        {
            write.Documents.Add(document);
            await write.SaveChangesAsync();
        }

        await using var read = _fixture.CreateContext();
        var loaded = await read.Documents.FirstAsync(d => d.Id == document.Id);

        loaded.CreatedBy.ShouldBe("system");
    }

    [Fact]
    public async Task Domain_timestamps_are_not_overwritten_by_the_interceptor()
    {
        // CreatedAt comes from the domain, which received it as a parameter (ADR-0008).
        // If the interceptor stamped it too, one business operation would carry a
        // timestamp different from the one its own domain event recorded.
        var tenant = TestFixtures.NewTenant();
        var document = TestFixtures.NewDocument(tenant);

        await using (var write = _fixture.CreateContext())
        {
            write.Documents.Add(document);
            await write.SaveChangesAsync();
        }

        await using var read = _fixture.CreateContext();
        var loaded = await read.Documents.FirstAsync(d => d.Id == document.Id);

        loaded.CreatedAt.ShouldBe(TestFixtures.Now);
    }

    // ── Domain events ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Domain_events_are_dispatched_after_a_successful_commit()
    {
        var tenant = TestFixtures.NewTenant();
        var recorder = new RecordingDomainEventDispatcher();

        await using var context = _fixture.CreateContext(dispatcher: recorder);
        var document = TestFixtures.CompletedDocument(tenant, chunkCount: 2);

        context.Documents.Add(document);
        await context.SaveChangesAsync();

        var ingested = recorder.Dispatched
            .OfType<DocumentIngestedEvent>()
            .ShouldHaveSingleItem();

        ingested.DocumentId.ShouldBe(document.Id);
        ingested.ChunkCount.ShouldBe(2);
    }

    [Fact]
    public async Task Events_are_cleared_so_a_second_save_does_not_redispatch_them()
    {
        // Without clearing, a handler that itself calls SaveChangesAsync would re-raise
        // the same events — an infinite loop that only appears once real handlers exist.
        var tenant = TestFixtures.NewTenant();
        var recorder = new RecordingDomainEventDispatcher();

        await using var context = _fixture.CreateContext(dispatcher: recorder);
        var document = TestFixtures.CompletedDocument(tenant, chunkCount: 2);

        context.Documents.Add(document);
        await context.SaveChangesAsync();
        recorder.Dispatched.Count.ShouldBe(1);

        document.Rename(new DocumentTitle("Renamed"), TestFixtures.Now.AddMinutes(1));
        await context.SaveChangesAsync();

        recorder.Dispatched.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_commit_dispatches_nothing()
    {
        // The ordering rule that matters: nobody is told a document is searchable when the
        // transaction that would have made it searchable rolled back.
        var tenant = TestFixtures.NewTenant();
        var recorder = new RecordingDomainEventDispatcher();

        await using var context = _fixture.CreateContext(dispatcher: recorder);
        var document = TestFixtures.CompletedDocument(tenant, chunkCount: 2);
        context.Documents.Add(document);

        // Force the commit to fail: a duplicate chunk index violates the unique index.
        context.DocumentChunks.Add(TestFixtures.NewChunk(document.Id, index: 0));

        await Should.ThrowAsync<DbUpdateException>(
            async () => await context.SaveChangesAsync());

        recorder.DispatchCallCount.ShouldBe(0);
        recorder.Dispatched.ShouldBeEmpty();
    }

    [Fact]
    public async Task Saving_without_events_does_not_call_the_dispatcher()
    {
        var tenant = TestFixtures.NewTenant();
        var recorder = new RecordingDomainEventDispatcher();

        await using var context = _fixture.CreateContext(dispatcher: recorder);
        context.Documents.Add(TestFixtures.NewDocument(tenant));
        await context.SaveChangesAsync();

        recorder.DispatchCallCount.ShouldBe(0);
    }
}
