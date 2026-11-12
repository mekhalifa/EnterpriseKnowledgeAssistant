using EKA.Domain.Ingestion;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace EKA.IntegrationTests.Persistence;

/// <summary>
/// Proves the two invariants Step 2 moved out of the aggregates are actually enforced.
/// </summary>
/// <remarks>
/// These are the most important tests in this step. An invariant that was relocated but
/// never verified in its new home is an invariant that silently stopped existing — and the
/// documentation saying it moved makes that worse, because it reads as reassurance.
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class RelocatedInvariantTests
{
    private readonly SqlServerFixture _fixture;

    public RelocatedInvariantTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    // ── Invariant 1: chunk indexes are unique per document ──────────────────────────

    [Fact]
    public async Task Two_chunks_cannot_share_an_index_within_one_document()
    {
        var tenant = TestFixtures.NewTenant();

        await using var context = _fixture.CreateContext();
        var document = TestFixtures.NewDocument(tenant);
        context.Documents.Add(document);
        await context.SaveChangesAsync();

        // Added straight to the DbSet, deliberately bypassing Document.AddChunks — which
        // is exactly what Step 8's bulk write path will do for large documents, and what a
        // retried job racing the original would do accidentally.
        context.DocumentChunks.Add(TestFixtures.NewChunk(document.Id, index: 0));
        context.DocumentChunks.Add(TestFixtures.NewChunk(document.Id, index: 0));

        var act = async () => await context.SaveChangesAsync();

        // The database refuses, so a bypassed aggregate cannot corrupt chunk ordering.
        await Should.ThrowAsync<DbUpdateException>(act);
    }

    [Fact]
    public async Task The_same_chunk_index_in_different_documents_is_fine()
    {
        // The constraint is per document. Every document has a chunk 0.
        var tenant = TestFixtures.NewTenant();

        await using var context = _fixture.CreateContext();
        var first = TestFixtures.NewDocument(tenant, "First");
        var second = TestFixtures.NewDocument(tenant, "Second");
        context.Documents.AddRange(first, second);
        await context.SaveChangesAsync();

        context.DocumentChunks.Add(TestFixtures.NewChunk(first.Id, index: 0));
        context.DocumentChunks.Add(TestFixtures.NewChunk(second.Id, index: 0));

        await Should.NotThrowAsync(async () => await context.SaveChangesAsync());
    }

    // ── Invariant 2: at most one ACTIVE prompt template per name ────────────────────

    [Fact]
    public async Task Two_active_versions_of_one_template_are_rejected()
    {
        var name = $"rag-answer-{Guid.NewGuid():N}";

        await using var context = _fixture.CreateContext();
        var provider = TestFixtures.NewProvider($"provider-{Guid.NewGuid():N}");
        context.AIProviders.Add(provider);

        var version1 = TestFixtures.NewTemplate(name, 1, provider.Id);
        var version2 = TestFixtures.NewTemplate(name, 2, provider.Id);
        version1.Activate(TestFixtures.Now);
        version2.Activate(TestFixtures.Now);

        context.PromptTemplates.AddRange(version1, version2);

        var act = async () => await context.SaveChangesAsync();

        // No aggregate can see this: Activate() only knows about itself. The filtered
        // unique index is the only place with a view of all rows.
        await Should.ThrowAsync<DbUpdateException>(act);
    }

    [Fact]
    public async Task Many_inactive_versions_of_one_template_are_allowed()
    {
        // This is why the index is filtered rather than plainly unique on Name — a plain
        // unique index would permit only one version ever, breaking versioning entirely.
        var name = $"rag-answer-{Guid.NewGuid():N}";

        await using var context = _fixture.CreateContext();
        var provider = TestFixtures.NewProvider($"provider-{Guid.NewGuid():N}");
        context.AIProviders.Add(provider);

        var version1 = TestFixtures.NewTemplate(name, 1, provider.Id);
        var version2 = TestFixtures.NewTemplate(name, 2, provider.Id);
        var version3 = TestFixtures.NewTemplate(name, 3, provider.Id);
        version3.Activate(TestFixtures.Now);

        context.PromptTemplates.AddRange(version1, version2, version3);

        await Should.NotThrowAsync(async () => await context.SaveChangesAsync());

        var stored = await context.PromptTemplates
            .Where(template => template.Name == name)
            .ToListAsync();

        stored.Count.ShouldBe(3);
        stored.Count(template => template.IsActive).ShouldBe(1);
    }

    [Fact]
    public async Task The_same_version_number_cannot_be_created_twice()
    {
        var name = $"rag-answer-{Guid.NewGuid():N}";

        await using var context = _fixture.CreateContext();
        var provider = TestFixtures.NewProvider($"provider-{Guid.NewGuid():N}");
        context.AIProviders.Add(provider);

        context.PromptTemplates.Add(TestFixtures.NewTemplate(name, 1, provider.Id));
        context.PromptTemplates.Add(TestFixtures.NewTemplate(name, 1, provider.Id));

        await Should.ThrowAsync<DbUpdateException>(
            async () => await context.SaveChangesAsync());
    }

    // ── Cascade behaviour ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Deleting_a_document_deletes_its_chunks()
    {
        // Chunks have no meaning without their document, and orphaned rows would be
        // returned by retrieval forever.
        var tenant = TestFixtures.NewTenant();

        await using var context = _fixture.CreateContext();
        var document = TestFixtures.CompletedDocument(tenant, chunkCount: 4);
        context.Documents.Add(document);
        await context.SaveChangesAsync();

        context.Documents.Remove(document);
        await context.SaveChangesAsync();

        var remaining = await context.DocumentChunks
            .CountAsync(chunk => chunk.DocumentId == document.Id);

        remaining.ShouldBe(0);
    }

    [Fact]
    public async Task A_tracked_template_blocks_its_provider_from_being_removed_client_side()
    {
        // Restrict, not cascade: deleting a provider must fail loudly rather than silently
        // delete the prompt engineering work referencing it.
        //
        // Note WHERE this fails. When the dependent is tracked, EF's change tracker
        // detects that a required foreign key would be severed and throws from Remove()
        // itself — before any SQL is generated. So the exception is
        // InvalidOperationException at Remove(), NOT DbUpdateException at save time.
        var name = $"rag-answer-{Guid.NewGuid():N}";

        await using var context = _fixture.CreateContext();
        var provider = TestFixtures.NewProvider($"provider-{Guid.NewGuid():N}");
        context.AIProviders.Add(provider);
        context.PromptTemplates.Add(TestFixtures.NewTemplate(name, 1, provider.Id));
        await context.SaveChangesAsync();

        var act = () => context.AIProviders.Remove(provider);

        Should.Throw<InvalidOperationException>(act)
            .Message.ShouldContain("severed");
    }

    [Fact]
    public async Task The_database_blocks_the_delete_even_when_EF_cannot_see_the_template()
    {
        // The guarantee that actually matters. EF's client-side check only works when the
        // dependent happens to be tracked; here the template was written by a different
        // context, so EF knows nothing about it and issues the DELETE. SQL Server's
        // foreign key is what refuses — which is also what protects against a migration
        // script, a DBA, or any client that is not this application.
        var name = $"rag-answer-{Guid.NewGuid():N}";
        var provider = TestFixtures.NewProvider($"provider-{Guid.NewGuid():N}");

        await using (var seed = _fixture.CreateContext())
        {
            seed.AIProviders.Add(provider);
            seed.PromptTemplates.Add(TestFixtures.NewTemplate(name, 1, provider.Id));
            await seed.SaveChangesAsync();
        }

        await using var context = _fixture.CreateContext();
        var reloaded = await context.AIProviders.FirstAsync(p => p.Id == provider.Id);

        context.AIProviders.Remove(reloaded);

        await Should.ThrowAsync<DbUpdateException>(
            async () => await context.SaveChangesAsync());
    }
}
