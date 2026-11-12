using EKA.Application.Documents.DeleteDocument;
using EKA.Application.Documents.GetDocument;
using EKA.Application.Documents.ListDocuments;
using EKA.Application.Documents.RegisterDocument;
using EKA.Application.Documents.RenameDocument;
using EKA.Application.Documents.ResetDocument;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace EKA.Application;

/// <summary>
/// Registers the use-case handlers and their validators.
/// </summary>
/// <remarks>
/// <para>
/// <b>Handlers are listed by hand, not scanned for.</b> Assembly scanning would be three
/// lines instead of six, and it would also mean that deleting a handler still compiles, a
/// typo in a namespace silently unregisters one, and the only way to answer "what does this
/// application do?" is to run it. The list below is that answer, and it fails at compile
/// time when it goes stale.
/// </para>
/// <para>
/// <b>Validators are scanned for</b>, because the opposite trade-off applies: a validator
/// that is never resolved does not break the build, it just stops validating, and a missing
/// registration is invisible until a bad request gets through. Scanning means adding a
/// validator is enough to make it run. <c>includeInternalTypes</c> is off: only public
/// validators are registered, so an internal helper cannot accidentally become a rule.
/// </para>
/// <para>
/// <b>Scoped, not transient or singleton.</b> Handlers hold a repository and a unit of work
/// that are themselves scoped to the request's <c>DbContext</c>; registering a handler as a
/// singleton would capture that <c>DbContext</c> forever, which is the classic captive
/// dependency bug — it works in development and corrupts state under concurrency.
/// </para>
/// </remarks>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The single place "now" enters the system. Registered rather than called
        // statically so a test can substitute a FakeTimeProvider and assert on exact
        // instants — the application-layer half of ADR-0008.
        services.AddSingleton(TimeProvider.System);

        services.AddScoped<RegisterDocumentHandler>();
        services.AddScoped<GetDocumentHandler>();
        services.AddScoped<ListDocumentsHandler>();
        services.AddScoped<RenameDocumentHandler>();
        services.AddScoped<ResetDocumentHandler>();
        services.AddScoped<DeleteDocumentHandler>();

        // Taken from the marker's assembly rather than `...Containing<TMarker>()`, which
        // cannot accept a static class as a type argument.
        services.AddValidatorsFromAssembly(
            typeof(ApplicationAssemblyMarker).Assembly,
            lifetime: ServiceLifetime.Singleton,
            includeInternalTypes: false);

        return services;
    }
}
