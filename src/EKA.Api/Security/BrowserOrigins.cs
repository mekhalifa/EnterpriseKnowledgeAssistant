namespace EKA.Api.Security;

/// <summary>
/// Cross-origin access for the browser client (Step 13): the <c>Cors</c> section.
/// </summary>
/// <remarks>
/// <para>
/// <b>What CORS is and is not.</b> It is the browser asking the API "may a page from this origin
/// read your responses?". It protects users from <i>other</i> sites' pages calling the API with
/// their credentials; it does not authenticate anything, and a non-browser client ignores it.
/// Authorization stays where it was: the bearer token, checked on every request.
/// </para>
/// <para>
/// <b>Why an explicit list and never <c>*</c>.</b> Bearer tokens are not sent automatically, so a
/// wildcard would not leak them by itself. But the list is the record of which front ends are
/// meant to exist, and a wildcard turns "unknown page reads our API" from an error into a
/// non-event. It is refused at startup, as is an origin with a path (browsers send none, so it
/// would match nothing and fail silently).
/// </para>
/// <para>
/// No credentials mode: the client sends <c>Authorization</c> headers, not cookies, so
/// <c>AllowCredentials</c> is off. A backend-for-frontend would change that (ADR-0033).
/// </para>
/// <para>
/// Empty list (the default outside Development): no cross-origin access at all, which is right
/// when the client is served from the API's own origin behind a reverse proxy.
/// </para>
/// </remarks>
internal static class BrowserOrigins
{
    public const string SectionName = "Cors";

    public static void AddEkaCors(this WebApplicationBuilder builder)
    {
        var origins = builder.Configuration.GetSection($"{SectionName}:AllowedOrigins").Get<string[]>() ?? [];

        foreach (var origin in origins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
                || uri.AbsolutePath != "/"
                || origin.EndsWith('/')
                || !string.IsNullOrEmpty(uri.Query))
            {
                throw new InvalidOperationException(
                    $"'{SectionName}:AllowedOrigins' entry '{origin}' is not an origin. "
                    + "Use scheme://host[:port] with no path or trailing slash, e.g. http://localhost:4200. "
                    + "A wildcard is not allowed.");
            }
        }

        builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(origins)
            .WithMethods(HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Delete)
            .WithHeaders("Authorization", "Content-Type", "Accept")
            // Readable by the page only when exposed: Retry-After drives the client's 429 message,
            // Location is where an upload or a new conversation lives.
            .WithExposedHeaders("Retry-After", "Location")
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));
    }
}
