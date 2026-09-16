using Microsoft.AspNetCore.Identity;

namespace Amicus.Infrastructure.Identity;

/// <summary>
/// Credentials and profile for anyone who signs in.
///
/// ASP.NET Core Identity is used rather than a hand-rolled user table because it
/// gives both halves of what we need out of the box: local email + password, and
/// external logins (Google) through the same account via AspNetUserLogins.
/// </summary>
public class AppUser : IdentityUser<Guid>
{
    public string? DisplayName { get; set; }

    /// <summary>
    /// Avatar URL. Populated from a Google sign-in's `picture` claim; null for a
    /// password account (which has no picture), where the clients fall back to
    /// initials. There is no upload flow — a student association doesn't need the
    /// storage/moderation that would bring.
    /// </summary>
    public string? PhotoUrl { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
