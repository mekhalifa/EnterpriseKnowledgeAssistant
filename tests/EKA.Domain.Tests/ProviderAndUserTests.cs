using EKA.Domain.Common;
using EKA.Domain.Identity;
using EKA.Domain.Providers;
using Shouldly;

namespace EKA.Domain.Tests;

public sealed class ProviderAndUserTests
{
    private static AIProvider OpenAI() =>
        new(
            Guid.NewGuid(),
            "openai",
            AIProviderKind.OpenAI,
            chatModel: "gpt-5",
            embeddingModel: "text-embedding-3-large",
            embeddingDimensions: 3072,
            secretReference: "Eka:Providers:OpenAI:ApiKey",
            TestData.Now);

    private static User NewUser() =>
        new(
            Guid.NewGuid(),
            TestData.Tenant,
            externalId: "d4f1c2e0-aaaa-bbbb-cccc-000000000001",
            email: "Amina.Yusuf@Example.COM",
            displayName: "Amina Yusuf",
            TestData.Now);

    // ── Structural guard: credentials must have nowhere to live ─────────────────────

    [Theory]
    [InlineData("ApiKey")]
    [InlineData("Key")]
    [InlineData("Secret")]
    [InlineData("Password")]
    [InlineData("Token")]
    [InlineData("ConnectionString")]
    public void AIProvider_has_no_property_that_could_hold_a_credential(string forbidden)
    {
        // A secret in a database column is a secret in every backup, every replica, every
        // restored developer snapshot, and the first thing read after any SQL injection.
        // This test exists so that adding such a property is a build failure rather than a
        // code review someone might wave through.
        var properties = typeof(AIProvider).GetProperties().Select(p => p.Name);

        properties.ShouldNotContain(forbidden);
    }

    [Fact]
    public void User_has_no_property_that_could_hold_a_password()
    {
        // Authentication belongs to the identity provider; this entity is only a local
        // projection of an account that lives elsewhere.
        var properties = typeof(User).GetProperties().Select(p => p.Name).ToList();

        properties.ShouldNotContain("PasswordHash");
        properties.ShouldNotContain("Password");
        properties.ShouldNotContain("Salt");
    }

    // ── AIProvider ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_provider_stores_the_reference_to_its_secret_not_the_secret()
    {
        var provider = OpenAI();

        provider.SecretReference.ShouldBe("Eka:Providers:OpenAI:ApiKey");
        provider.IsEnabled.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_provider_must_name_the_secret_holding_its_credential(string reference)
    {
        var act = () => new AIProvider(
            Guid.NewGuid(), "openai", AIProviderKind.OpenAI, "gpt-5",
            "text-embedding-3-large", 3072, reference, TestData.Now);

        Should.Throw<DomainException>(act).Message.ShouldContain("never the credential");
    }

    [Fact]
    public void Azure_OpenAI_requires_an_endpoint()
    {
        // An Azure deployment is reached at a tenant-specific host; there is no shared
        // default. Failing here beats a confusing 404 on the first chat request.
        var act = () => new AIProvider(
            Guid.NewGuid(), "azure", AIProviderKind.AzureOpenAI, "gpt-5",
            "text-embedding-3-large", 3072, "Eka:Azure:ApiKey", TestData.Now);

        Should.Throw<DomainException>(act).Message.ShouldContain("endpoint");
    }

    [Fact]
    public void Azure_OpenAI_is_accepted_with_an_endpoint()
    {
        var provider = new AIProvider(
            Guid.NewGuid(), "azure", AIProviderKind.AzureOpenAI, "gpt-5",
            "text-embedding-3-large", 3072, "Eka:Azure:ApiKey", TestData.Now,
            endpoint: "https://contoso.openai.azure.com/");

        provider.Endpoint.ShouldBe("https://contoso.openai.azure.com/");
    }

    [Fact]
    public void OpenAI_needs_no_endpoint()
    {
        OpenAI().Endpoint.ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1536)]
    public void Embedding_dimensions_must_be_positive(int dimensions)
    {
        var act = () => new AIProvider(
            Guid.NewGuid(), "openai", AIProviderKind.OpenAI, "gpt-5",
            "text-embedding-3-large", dimensions, "Eka:OpenAI:ApiKey", TestData.Now);

        Should.Throw<DomainException>(act);
    }

    [Fact]
    public void A_provider_can_be_disabled_and_re_enabled()
    {
        var provider = OpenAI();

        provider.Disable(TestData.Later);
        provider.IsEnabled.ShouldBeFalse();
        provider.ModifiedAt.ShouldBe(TestData.Later);

        provider.Enable(TestData.Later);
        provider.IsEnabled.ShouldBeTrue();
    }

    [Fact]
    public void The_chat_model_can_be_changed_safely()
    {
        // Unlike the embedding model, changing which model writes answers invalidates
        // nothing already stored.
        var provider = OpenAI();

        provider.UseChatModel("gpt-5-mini", TestData.Later);

        provider.ChatModel.ShouldBe("gpt-5-mini");
        provider.EmbeddingModel.ShouldBe("text-embedding-3-large");
    }

    [Fact]
    public void A_provider_requires_both_a_chat_and_an_embedding_model()
    {
        Should.Throw<DomainException>(() => new AIProvider(
            Guid.NewGuid(), "openai", AIProviderKind.OpenAI, "  ",
            "text-embedding-3-large", 3072, "ref", TestData.Now));

        Should.Throw<DomainException>(() => new AIProvider(
            Guid.NewGuid(), "openai", AIProviderKind.OpenAI, "gpt-5",
            "  ", 3072, "ref", TestData.Now));
    }

    // ── User ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Email_is_lowercased_for_comparison()
    {
        NewUser().Email.ShouldBe("amina.yusuf@example.com");
    }

    [Fact]
    public void Identity_is_keyed_on_the_providers_subject_claim_not_the_email()
    {
        // Emails get reassigned when people leave. Keying on one eventually hands a
        // departed employee's conversation history to whoever inherits the address.
        var user = NewUser();

        user.ExternalId.ShouldBe("d4f1c2e0-aaaa-bbbb-cccc-000000000001");
    }

    [Fact]
    public void A_user_must_have_a_tenant_subject_email_and_display_name()
    {
        Should.Throw<DomainException>(() => new User(
            Guid.NewGuid(), default, "sub", "a@b.com", "A B", TestData.Now));

        Should.Throw<DomainException>(() => new User(
            Guid.NewGuid(), TestData.Tenant, "  ", "a@b.com", "A B", TestData.Now));

        Should.Throw<DomainException>(() => new User(
            Guid.NewGuid(), TestData.Tenant, "sub", "  ", "A B", TestData.Now));

        Should.Throw<DomainException>(() => new User(
            Guid.NewGuid(), TestData.Tenant, "sub", "a@b.com", "  ", TestData.Now));
    }

    [Fact]
    public void Roles_are_matched_case_insensitively()
    {
        var user = NewUser();

        user.GrantRole("HR.Reader", TestData.Now);

        user.IsInRole("hr.reader").ShouldBeTrue();
        user.IsInRole("HR.READER").ShouldBeTrue();
        user.IsInRole("Finance.Reader").ShouldBeFalse();
    }

    [Fact]
    public void Granting_the_same_role_twice_does_not_duplicate_it()
    {
        var user = NewUser();

        user.GrantRole("HR.Reader", TestData.Now);
        user.GrantRole("hr.reader", TestData.Later);

        user.Roles.Count.ShouldBe(1);
    }

    [Fact]
    public void Revoking_a_role_removes_it_regardless_of_casing()
    {
        var user = NewUser();
        user.GrantRole("HR.Reader", TestData.Now);

        user.RevokeRole("hr.READER", TestData.Later);

        user.Roles.ShouldBeEmpty();
    }

    [Fact]
    public void Revoking_a_role_the_user_does_not_have_is_a_no_op()
    {
        var user = NewUser();

        Should.NotThrow(() => user.RevokeRole("Nonexistent", TestData.Later));
        user.ModifiedAt.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_role_name_cannot_be_blank(string role)
    {
        Should.Throw<DomainException>(() => NewUser().GrantRole(role, TestData.Now));
    }

    [Fact]
    public void Users_are_deactivated_rather_than_deleted()
    {
        // Deleting would orphan the conversations and audit records that reference them —
        // exactly what a security review asks to see.
        var user = NewUser();

        user.Deactivate(TestData.Later);
        user.IsActive.ShouldBeFalse();

        user.Reactivate(TestData.Later);
        user.IsActive.ShouldBeTrue();
    }
}
