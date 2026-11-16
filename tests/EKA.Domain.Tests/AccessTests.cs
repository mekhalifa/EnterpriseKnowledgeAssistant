using EKA.Domain.Access;
using EKA.Domain.Common;
using Shouldly;

namespace EKA.Domain.Tests;

public sealed class AccessPrincipalTests
{
    [Theory]
    [InlineData("tenant", AccessPrincipalKind.Tenant, null)]
    [InlineData("user:alice", AccessPrincipalKind.User, "alice")]
    [InlineData("group:6f1c2e0a-hr", AccessPrincipalKind.Group, "6f1c2e0a-hr")]
    [InlineData("user:auth0|abc:def", AccessPrincipalKind.User, "auth0|abc:def")]
    public void The_canonical_key_reads_back_to_the_same_principal(string key, AccessPrincipalKind kind, string? id)
    {
        var principal = AccessPrincipal.Parse(key);

        principal.Kind.ShouldBe(kind);
        principal.Id.ShouldBe(id);
        principal.Key.ShouldBe(key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Tenant")]          // keys are exact, not case-insensitive
    [InlineData("user:")]
    [InlineData("user: alice")]     // never trimmed into validity
    [InlineData("user:alice ")]
    [InlineData("group:hr\n")]
    [InlineData("role:admin")]
    [InlineData("alice")]
    public void Anything_else_is_not_a_principal(string key)
    {
        AccessPrincipal.TryParse(key, out _).ShouldBeFalse();
        Should.Throw<DomainException>(() => AccessPrincipal.Parse(key));
    }

    [Fact]
    public void An_overlong_id_is_refused()
    {
        Should.Throw<DomainException>(() => AccessPrincipal.User(new string('a', AccessPrincipal.MaxIdLength + 1)));
        AccessPrincipal.User(new string('a', AccessPrincipal.MaxIdLength)).Id!.Length.ShouldBe(AccessPrincipal.MaxIdLength);
    }

    [Fact]
    public void Subject_ids_are_case_sensitive()
    {
        AccessPrincipal.User("Alice").ShouldNotBe(AccessPrincipal.User("alice"));
    }
}

public sealed class DocumentAccessTests
{
    private static DocumentAccess Of(params string[] keys) => DocumentAccess.Of(keys.Select(AccessPrincipal.Parse));

    [Fact]
    public void An_empty_list_is_refused_because_nobody_could_ever_read_or_recover_the_document()
    {
        Should.Throw<DomainException>(() => Of());
    }

    [Fact]
    public void Tenant_cannot_be_mixed_with_named_principals()
    {
        // [tenant, user:alice] reads as restricted and is not; removing "tenant" from it later
        // would cut off everyone but Alice.
        Should.Throw<DomainException>(() => Of("tenant", "user:alice"))
            .Message.ShouldContain("cannot be combined");
    }

    [Fact]
    public void The_list_is_distinct_and_sorted_so_equal_policies_store_identically()
    {
        Of("user:bob", "group:hr", "user:bob").Keys.ShouldBe(["group:hr", "user:bob"]);
        Of("group:hr", "user:bob").Keys.ShouldBe(Of("user:bob", "group:hr").Keys);
    }

    [Fact]
    public void More_principals_than_the_limit_are_refused_in_favour_of_a_group()
    {
        var keys = Enumerable.Range(0, DocumentAccess.MaxPrincipals + 1).Select(i => $"user:u{i}").ToArray();

        Should.Throw<DomainException>(() => Of(keys)).Message.ShouldContain("group");
        Of(keys[..DocumentAccess.MaxPrincipals]).Principals.Count.ShouldBe(DocumentAccess.MaxPrincipals);
    }

    [Fact]
    public void Tenant_alone_is_the_tenant_wide_policy()
    {
        Of("tenant").IsTenantWide.ShouldBeTrue();
        Of("group:hr").IsTenantWide.ShouldBeFalse();
    }

    [Fact]
    public void A_policy_grants_a_scope_holding_any_one_of_its_principals()
    {
        var hrOrBob = Of("group:hr", "user:bob");

        hrOrBob.Grants(AccessScope.For(TestData.Tenant, "bob")).ShouldBeTrue();
        hrOrBob.Grants(AccessScope.For(TestData.Tenant, "carol", ["finance", "hr"])).ShouldBeTrue();
        hrOrBob.Grants(AccessScope.For(TestData.Tenant, "dave", ["finance"])).ShouldBeFalse();
        DocumentAccess.TenantWide.Grants(AccessScope.For(TestData.Tenant, "anyone")).ShouldBeTrue();
    }
}

public sealed class AccessScopeTests
{
    [Fact]
    public void A_scope_always_holds_the_tenant_and_its_own_user()
    {
        AccessScope.For(TestData.Tenant, "alice").Keys.ShouldBe(["tenant", "user:alice"]);
    }

    [Fact]
    public void Groups_follow_and_blank_or_repeated_ones_are_ignored()
    {
        AccessScope.For(TestData.Tenant, "alice", ["hr", " ", "", "hr", "finance"]).Keys
            .ShouldBe(["tenant", "user:alice", "group:hr", "group:finance"]);
    }

    [Fact]
    public void A_scope_without_a_tenant_is_refused()
    {
        Should.Throw<DomainException>(() => AccessScope.For(default, "alice"));
    }
}

public sealed class DocumentAccessControlTests
{
    private static readonly TenantId OtherTenant = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public void A_new_document_is_readable_by_its_whole_tenant()
    {
        var document = TestData.NewDocument();

        document.Access.IsTenantWide.ShouldBeTrue();
        document.AccessKeys.ShouldBe(["tenant"]);
        document.IsAccessibleTo(AccessScope.For(TestData.Tenant, "anyone")).ShouldBeTrue();
    }

    [Fact]
    public void No_principal_opens_a_document_in_another_tenant()
    {
        // group ids are unique only inside one identity provider; "hr" in tenant B is not "hr" here.
        var document = TestData.NewDocument();
        document.ChangeAccess(DocumentAccess.Of([AccessPrincipal.Group("hr")]), TestData.Later);

        document.IsAccessibleTo(AccessScope.For(OtherTenant, "carol", ["hr"])).ShouldBeFalse();
        TestData.NewDocument().IsAccessibleTo(AccessScope.For(OtherTenant, "anyone")).ShouldBeFalse();
    }

    [Fact]
    public void Restricting_a_document_admits_only_the_named_users_and_group_members()
    {
        var document = TestData.NewDocument();

        document.ChangeAccess(DocumentAccess.Of([AccessPrincipal.Group("hr"), AccessPrincipal.User("olivia")]), TestData.Later);

        document.IsAccessibleTo(AccessScope.For(TestData.Tenant, "olivia")).ShouldBeTrue();
        document.IsAccessibleTo(AccessScope.For(TestData.Tenant, "carol", ["hr"])).ShouldBeTrue();
        document.IsAccessibleTo(AccessScope.For(TestData.Tenant, "bob")).ShouldBeFalse();
        document.AccessKeys.ShouldBe(["group:hr", "user:olivia"]);
    }

    [Fact]
    public void Changing_access_is_legal_in_any_state_and_records_the_change()
    {
        var document = TestData.DocumentIn(Ingestion.IngestionStatus.Completed);

        document.ChangeAccess(DocumentAccess.Of([AccessPrincipal.User("olivia")]), TestData.Later);

        document.ModifiedAt.ShouldBe(TestData.Later);
        document.Status.ShouldBe(Ingestion.IngestionStatus.Completed);
    }

    // ── Management (Step 12) ────────────────────────────────────────────────────────

    private static Ingestion.Document Owned(string owner = "olivia", params AccessPrincipal[] access) =>
        new(Guid.NewGuid(), TestData.Tenant, TestData.Title(), TestData.Pdf(), "k", TestData.Now,
            access: access.Length == 0 ? null : DocumentAccess.Of(access), ownerSubject: owner);

    [Fact]
    public void The_owner_and_an_administrator_may_manage_a_document_and_a_reader_may_not()
    {
        var document = Owned();

        document.CanBeManagedBy(AccessScope.For(TestData.Tenant, "olivia")).ShouldBeTrue();
        document.CanBeManagedBy(AccessScope.For(TestData.Tenant, "root", isAdministrator: true)).ShouldBeTrue();
        document.CanBeManagedBy(AccessScope.For(TestData.Tenant, "bob")).ShouldBeFalse();
        document.IsAccessibleTo(AccessScope.For(TestData.Tenant, "bob")).ShouldBeTrue(); // reads, cannot manage
    }

    [Fact]
    public void Administration_stops_at_the_tenant_boundary()
    {
        Owned().CanBeManagedBy(AccessScope.For(OtherTenant, "root", isAdministrator: true)).ShouldBeFalse();
        Owned().CanBeManagedBy(AccessScope.For(OtherTenant, "olivia")).ShouldBeFalse();
    }

    [Fact]
    public void An_administrator_may_manage_but_not_read_a_restricted_document()
    {
        // Administering and reading are separate privileges: the admin can repair the list,
        // and gets no content through search or answers unless the list grants it.
        var document = Owned("olivia", AccessPrincipal.Group("hr"));
        var admin = AccessScope.For(TestData.Tenant, "root", isAdministrator: true);

        document.CanBeManagedBy(admin).ShouldBeTrue();
        document.IsVisibleTo(admin).ShouldBeTrue();
        document.IsAccessibleTo(admin).ShouldBeFalse();
    }

    [Fact]
    public void An_owner_who_restricted_a_document_away_from_themselves_can_still_find_and_fix_it()
    {
        var document = Owned("olivia", AccessPrincipal.Group("hr"));
        var olivia = AccessScope.For(TestData.Tenant, "olivia");

        document.IsAccessibleTo(olivia).ShouldBeFalse();
        document.IsVisibleTo(olivia).ShouldBeTrue();
        document.CanBeManagedBy(olivia).ShouldBeTrue();
    }

    [Fact]
    public void An_unowned_document_is_managed_by_administrators_only()
    {
        var document = Owned(owner: null!);

        document.CanBeManagedBy(AccessScope.For(TestData.Tenant, "anyone")).ShouldBeFalse();
        document.CanBeManagedBy(AccessScope.For(TestData.Tenant, "root", isAdministrator: true)).ShouldBeTrue();
    }

    [Fact]
    public void Ownership_compares_subjects_exactly()
    {
        Owned("olivia").CanBeManagedBy(AccessScope.For(TestData.Tenant, "Olivia")).ShouldBeFalse();
    }

    [Fact]
    public void A_document_can_be_created_restricted()
    {
        var document = new Ingestion.Document(
            Guid.NewGuid(), TestData.Tenant, TestData.Title(), TestData.Pdf(), "k", TestData.Now,
            access: DocumentAccess.Of([AccessPrincipal.Group("board")]));

        document.AccessKeys.ShouldBe(["group:board"]);
    }
}
