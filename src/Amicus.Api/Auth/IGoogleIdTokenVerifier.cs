namespace Amicus.Api.Auth;

/// <summary>Identity Google vouches for, once its ID token has been verified.</summary>
public sealed record GoogleIdentity(
    string Subject, string Email, bool EmailVerified, string? Name, string? Picture);

/// <summary>
/// Abstracted so the sign-in logic — find-or-create the user, link the external
/// login, issue our token — is testable without a live Google round-trip.
/// </summary>
public interface IGoogleIdTokenVerifier
{
    /// <returns>The verified identity, or <c>null</c> if the token is not valid.</returns>
    Task<GoogleIdentity?> VerifyAsync(string idToken, CancellationToken cancellationToken = default);
}
