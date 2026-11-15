using EKA.Application.Common;

namespace EKA.Application.Identity;

internal static class UserErrors
{
    /// <summary>
    /// The identity provider vouched for the caller but did not say who they are well
    /// enough to create a local account. 403, not 401: authentication succeeded.
    /// </summary>
    public static readonly Error IncompleteIdentity = Error.Forbidden(
        "user.incomplete_identity",
        "Your sign-in did not include an email address, which this service requires.");

    public static readonly Error Deactivated = Error.Forbidden(
        "user.deactivated",
        "Your account has been deactivated.");
}
