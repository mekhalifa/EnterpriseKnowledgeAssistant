using EKA.Domain.Ingestion;
using Shouldly;

namespace EKA.Domain.Tests;

/// <summary>
/// Entities are equal by identity, value objects by value. Getting this wrong is how
/// duplicate rows and lost updates appear: a collection that cannot recognise two loads of
/// the same document as the same document will happily hold both.
/// </summary>
public sealed class EntityIdentityTests
{
    [Fact]
    public void Two_entities_with_the_same_id_are_the_same_entity()
    {
        var id = Guid.NewGuid();

        var one = TestData.NewDocument(id, TestData.Title("Original title"));
        var other = TestData.NewDocument(id, TestData.Title("Completely different title"));

        // Different titles, same identity. A document whose title was corrected is still
        // the same document — that is what being an entity means.
        one.ShouldBe(other);
        (one == other).ShouldBeTrue();
        one.GetHashCode().ShouldBe(other.GetHashCode());
    }

    [Fact]
    public void Two_entities_with_different_ids_are_different_entities()
    {
        var one = TestData.NewDocument();
        var other = TestData.NewDocument();

        one.ShouldNotBe(other);
        (one != other).ShouldBeTrue();
    }

    [Fact]
    public void Different_entity_types_are_never_equal()
    {
        var id = Guid.NewGuid();

        var document = TestData.NewDocument(id);
        var chunk = new DocumentChunk(
            id,
            Guid.NewGuid(),
            ChunkIndex.First,
            "text",
            default,
            TestData.Now);

        // A Document and a DocumentChunk that happen to share a Guid are not the same
        // thing. Comparing only ids would make them equal.
        document.Equals(chunk).ShouldBeFalse();
    }

    [Fact]
    public void An_entity_is_never_equal_to_null()
    {
        var document = TestData.NewDocument();

        document.Equals(null).ShouldBeFalse();
        (document == null).ShouldBeFalse();
        (null == document).ShouldBeFalse();
    }

    [Fact]
    public void Two_nulls_are_equal()
    {
        Document? left = null;
        Document? right = null;

        (left == right).ShouldBeTrue();
    }

    [Fact]
    public void Identity_equality_makes_set_semantics_work()
    {
        var id = Guid.NewGuid();

        HashSet<Document> set =
        [
            TestData.NewDocument(id),
            TestData.NewDocument(id),
        ];

        // Without identity equality this set would contain two copies of one document,
        // which is exactly how duplicate inserts happen.
        set.Count.ShouldBe(1);
    }

    [Fact]
    public void Audit_fields_record_creation_and_modification_separately()
    {
        var document = TestData.NewDocument();

        document.CreatedAt.ShouldBe(TestData.Now);
        document.ModifiedAt.ShouldBeNull();

        document.BeginExtraction(TestData.Later);

        document.CreatedAt.ShouldBe(TestData.Now);
        document.ModifiedAt.ShouldBe(TestData.Later);
    }

    [Fact]
    public void CreatedBy_is_left_for_the_persistence_layer_to_fill()
    {
        // The domain has no notion of "who is logged in"; an EF interceptor sets this in
        // Step 3 from the ambient user.
        TestData.NewDocument().CreatedBy.ShouldBeNull();
    }
}
