using EKA.Domain.Common;
using EKA.Domain.Prompting;
using Shouldly;

namespace EKA.Domain.Tests;

public sealed class PromptTemplateTests
{
    private const string GroundedPrompt = """
        Answer using only the context below. If the context is insufficient, say so.

        Context:
        {{context}}

        Question: {{question}}
        """;

    private static readonly Guid ProviderId = Guid.NewGuid();

    private static PromptTemplate NewTemplate(
        string? userPrompt = null,
        int version = 1,
        Guid? providerId = null) =>
        new(
            Guid.NewGuid(),
            "rag-answer",
            version,
            systemPrompt: "You are a careful enterprise assistant. Cite your sources.",
            userPrompt ?? GroundedPrompt,
            providerId ?? ProviderId,
            TestData.Now);

    // ── The placeholder invariant ───────────────────────────────────────────────────

    [Fact]
    public void A_user_prompt_without_the_question_placeholder_is_rejected()
    {
        // Without this guard the rendered prompt never contains the user's question. The
        // model answers something plausible from context alone, nothing errors, and the
        // failure looks like poor answer quality rather than a broken template.
        var act = () => NewTemplate(userPrompt: "Context:\n{{context}}\n\nAnswer please.");

        Should.Throw<DomainException>(act)
            .Message.ShouldContain(PromptTemplate.QuestionPlaceholder);
    }

    [Fact]
    public void A_template_with_the_question_placeholder_is_accepted()
    {
        Should.NotThrow(() => NewTemplate(userPrompt: "Question: {{question}}"));
    }

    [Fact]
    public void Grounding_support_is_derived_from_the_context_placeholder()
    {
        NewTemplate().SupportsGrounding.ShouldBeTrue();

        // Legitimate as a plain chat prompt, but it cannot produce grounded answers, so
        // Step 10 must refuse to use it for RAG.
        NewTemplate(userPrompt: "Question: {{question}}").SupportsGrounding.ShouldBeFalse();
    }

    [Fact]
    public void Prompt_whitespace_is_preserved_because_the_model_reads_it()
    {
        var template = NewTemplate();

        template.UserPromptTemplate.ShouldBe(GroundedPrompt);
    }

    // ── Required provider association (README P1) ───────────────────────────────────

    [Fact]
    public void A_template_must_declare_which_provider_it_was_written_for()
    {
        // A prompt tuned for one model is not portable: instruction-following, refusal
        // behaviour, and context handling all differ between models.
        var act = () => NewTemplate(providerId: Guid.Empty);

        Should.Throw<DomainException>(act).Message.ShouldContain("AI provider");
    }

    // ── Other guards ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Versions_start_at_one(int version)
    {
        Should.Throw<DomainException>(() => NewTemplate(version: version));
    }

    [Fact]
    public void A_template_requires_a_system_instruction()
    {
        var act = () => new PromptTemplate(
            Guid.NewGuid(), "rag-answer", 1, "   ", GroundedPrompt, ProviderId, TestData.Now);

        Should.Throw<DomainException>(act);
    }

    [Fact]
    public void A_template_requires_a_name()
    {
        var act = () => new PromptTemplate(
            Guid.NewGuid(), " ", 1, "system", GroundedPrompt, ProviderId, TestData.Now);

        Should.Throw<DomainException>(act);
    }

    // ── Activation ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_new_template_starts_inactive_so_it_can_be_reviewed_first()
    {
        NewTemplate().IsActive.ShouldBeFalse();
    }

    [Fact]
    public void Activating_and_deactivating_are_idempotent()
    {
        var template = NewTemplate();

        template.Activate(TestData.Now);
        template.IsActive.ShouldBeTrue();

        Should.NotThrow(() => template.Activate(TestData.Later));
        template.IsActive.ShouldBeTrue();

        template.Deactivate(TestData.Later);
        template.IsActive.ShouldBeFalse();

        Should.NotThrow(() => template.Deactivate(TestData.Later));
        template.IsActive.ShouldBeFalse();
    }

    // ── Versioning ──────────────────────────────────────────────────────────────────

    [Fact]
    public void The_next_version_increments_and_inherits_name_and_provider()
    {
        var original = NewTemplate();
        original.Activate(TestData.Now);

        var next = original.CreateNextVersion(
            Guid.NewGuid(),
            systemPrompt: "You are a careful assistant. Refuse when evidence is thin.",
            userPromptTemplate: GroundedPrompt,
            TestData.Later);

        next.Version.ShouldBe(2);
        next.Name.ShouldBe(original.Name);
        next.AIProviderId.ShouldBe(original.AIProviderId);

        // New wording starts inactive and the original keeps serving traffic until the new
        // version is evaluated. This is why templates are versioned rather than edited.
        next.IsActive.ShouldBeFalse();
        original.IsActive.ShouldBeTrue();
        original.Version.ShouldBe(1);
    }

    [Fact]
    public void A_next_version_is_validated_like_any_other()
    {
        var original = NewTemplate();

        var act = () => original.CreateNextVersion(
            Guid.NewGuid(), "system", "no placeholder here", TestData.Later);

        Should.Throw<DomainException>(act);
    }

    [Fact]
    public void ToString_shows_the_version_and_whether_it_is_live()
    {
        var template = NewTemplate();

        template.ToString().ShouldBe("rag-answer v1");

        template.Activate(TestData.Now);
        template.ToString().ShouldBe("rag-answer v1 (active)");
    }
}
