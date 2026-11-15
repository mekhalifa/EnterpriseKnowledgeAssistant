using EKA.Domain.Conversations;
using EKA.Domain.Ingestion;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace EKA.IntegrationTests.Persistence;

/// <summary>
/// A child added to an aggregate that was <i>loaded</i>, not created in the same context, is
/// inserted, not updated.
/// </summary>
/// <remarks>
/// <para>
/// The bug these guard (step-06 §5.1): by EF Core convention a Guid key is store-generated,
/// so a new child arriving with a domain-generated id looks like an existing row. EF
/// issued an UPDATE that touched nothing and threw <c>DbUpdateConcurrencyException</c>.
/// </para>
/// <para>
/// The round-trip tests could not see it: there the root is new, so EF marks the whole graph
/// Added. This is the shape real use cases have: load, change, save. Conversations hit it in
/// Step 6, and document chunks would have hit it in Step 8.
/// </para>
/// </remarks>
[Collection(SqlServerCollection.Name)]
public sealed class ChildInsertTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task A_message_added_to_a_loaded_conversation_is_inserted()
    {
        var conversation = new Conversation(Guid.CreateVersion7(), TestFixtures.NewTenant(), Guid.NewGuid(), TestFixtures.Now);

        await using (var create = fixture.CreateContext())
        {
            create.Conversations.Add(conversation);
            await create.SaveChangesAsync();
        }

        await using (var update = fixture.CreateContext())
        {
            var loaded = await update.Conversations
                .Include(c => c.Messages)
                .SingleAsync(c => c.Id == conversation.Id);

            loaded.AddUserMessage(
                Message.FromUser(Guid.CreateVersion7(), loaded.Id, "a question", TestFixtures.Now),
                TestFixtures.Now);

            await Should.NotThrowAsync(() => update.SaveChangesAsync());
        }

        await using var read = fixture.CreateContext();
        (await read.Messages.CountAsync(m => m.ConversationId == conversation.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task Chunks_added_to_a_loaded_document_are_inserted()
    {
        var document = TestFixtures.NewDocument(TestFixtures.NewTenant());

        await using (var create = fixture.CreateContext())
        {
            create.Documents.Add(document);
            await create.SaveChangesAsync();
        }

        await using (var update = fixture.CreateContext())
        {
            var loaded = await update.Documents
                .Include(d => d.Chunks)
                .SingleAsync(d => d.Id == document.Id);

            loaded.BeginExtraction(TestFixtures.Now);
            loaded.CompleteExtraction(TestFixtures.Now);
            loaded.AddChunks([TestFixtures.NewChunk(loaded.Id, 0), TestFixtures.NewChunk(loaded.Id, 1)], TestFixtures.Now);

            await Should.NotThrowAsync(() => update.SaveChangesAsync());
        }

        await using var read = fixture.CreateContext();
        (await read.DocumentChunks.CountAsync(c => c.DocumentId == document.Id)).ShouldBe(2);
    }
}
