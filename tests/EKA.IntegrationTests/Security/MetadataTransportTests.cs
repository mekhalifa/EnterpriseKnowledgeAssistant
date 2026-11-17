using EKA.Api.Security;
using Shouldly;

namespace EKA.IntegrationTests.Security;

/// <summary>
/// Where the API may fetch its signing keys from. Until Step 14 this rule was "asserted by code
/// review"; it is a pure function now, and these are its tests.
/// </summary>
public sealed class MetadataTransportTests
{
    private static JwtAuthenticationOptions Options(
        bool requireHttps = false,
        bool allowPrivate = false,
        string authority = "http://127.0.0.1:8080/realms/eka",
        string? metadata = null) => new()
        {
            Authority = authority,
            Audience = "eka-api",
            RequireHttpsMetadata = requireHttps,
            AllowHttpMetadataOnPrivateNetwork = allowPrivate,
            MetadataAddress = metadata,
        };

    [Fact]
    public void Https_metadata_is_always_acceptable() =>
        JwtAuthentication.MetadataTransportProblem(Options(requireHttps: true), isDevelopment: false).ShouldBeNull();

    [Fact]
    public void Development_may_fetch_keys_over_http_from_a_local_identity_provider() =>
        JwtAuthentication.MetadataTransportProblem(Options(), isDevelopment: true).ShouldBeNull();

    [Fact]
    public void Http_metadata_outside_development_is_refused_without_the_explicit_opt_in() =>
        JwtAuthentication.MetadataTransportProblem(
                Options(metadata: "http://keycloak:8080/realms/eka/.well-known/openid-configuration"),
                isDevelopment: false)
            .ShouldNotBeNull();

    [Fact]
    public void The_opt_in_admits_a_container_service_name() =>
        JwtAuthentication.MetadataTransportProblem(
                Options(allowPrivate: true, metadata: "http://keycloak:8080/realms/eka/.well-known/openid-configuration"),
                isDevelopment: false)
            .ShouldBeNull();

    [Theory]
    [InlineData("http://login.example.com/realms/eka/.well-known/openid-configuration")]
    [InlineData("http://10.0.0.5:8080/realms/eka/.well-known/openid-configuration")]
    [InlineData("http://localhost:8080/realms/eka/.well-known/openid-configuration")]
    public void The_opt_in_never_admits_a_routable_or_loopback_host(string metadata) =>
        JwtAuthentication.MetadataTransportProblem(Options(allowPrivate: true, metadata: metadata), isDevelopment: false)
            .ShouldNotBeNull();

    [Fact]
    public void Without_a_metadata_address_the_authority_is_what_is_judged() =>
        JwtAuthentication.MetadataTransportProblem(Options(allowPrivate: true), isDevelopment: false)
            .ShouldNotBeNull();
}
