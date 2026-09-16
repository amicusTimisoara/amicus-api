using Amicus.Api.Auth;
using Amicus.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Amicus.Api.Endpoints;

public sealed record GoogleSignInRequest(string IdToken);

public static class GoogleAuthEndpoints
{
    public const string ProviderName = "Google";

    /// <summary>
    /// Client-side ID-token flow, deliberately, rather than a server-side redirect:
    /// the web SPA and both mobile platforms all obtain an ID token from Google's
    /// own SDK and POST it here. That avoids redirect URIs, deep links and custom
    /// URL schemes entirely, and behaves identically on every client.
    ///
    /// Returns the same <c>AccessTokenResponse</c> shape as <c>/auth/login</c>, so a
    /// client stores and refreshes tokens the same way however the user signed in.
    /// </summary>
    public static RouteGroupBuilder MapGoogleAuth(this RouteGroupBuilder group)
    {
        group.MapPost("/google", async (
            [FromBody] GoogleSignInRequest request,
            IGoogleIdTokenVerifier verifier,
            UserManager<AppUser> users,
            SignInManager<AppUser> signIn,
            ILogger<AppUser> logger,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.IdToken))
            {
                return Results.BadRequest(new { error = "idToken is required." });
            }

            var identity = await verifier.VerifyAsync(request.IdToken, cancellationToken);

            if (identity is null)
            {
                return Results.Unauthorized();
            }

            // We match accounts by email below. An unverified Google email would let
            // anyone who can set their profile address to a victim's take over that
            // victim's account, so it is refused outright.
            if (!identity.EmailVerified)
            {
                logger.LogWarning(
                    "Google sign-in refused: address not verified with Google.");
                return Results.Problem(
                    title: "Email not verified with Google.",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var user = await users.FindByLoginAsync(ProviderName, identity.Subject);

            // Keep the profile fresh: Google photos and names change, so mirror the
            // latest on every sign-in for a user we already know.
            if (user is not null
                && (user.PhotoUrl != identity.Picture || user.DisplayName != identity.Name))
            {
                user.PhotoUrl = identity.Picture;
                user.DisplayName = identity.Name;
                await users.UpdateAsync(user);
            }

            if (user is null)
            {
                user = await users.FindByEmailAsync(identity.Email);

                if (user is null)
                {
                    user = new AppUser
                    {
                        Id = Guid.CreateVersion7(),
                        UserName = identity.Email,
                        Email = identity.Email,
                        // Google verified it; making the student re-confirm by email
                        // would be theatre.
                        EmailConfirmed = true,
                        DisplayName = identity.Name,
                        PhotoUrl = identity.Picture,
                        CreatedAt = DateTimeOffset.UtcNow,
                    };

                    var created = await users.CreateAsync(user);

                    if (!created.Succeeded)
                    {
                        return Results.ValidationProblem(created.Errors.ToDictionary(
                            e => e.Code, e => new[] { e.Description }));
                    }
                }

                // Links Google onto an account that may already have a password. The
                // address was verified by Google, so it is the same person — this is
                // what stops a student who registered with a password from being
                // locked out of, or duplicated by, signing in with Google later.
                var linked = await users.AddLoginAsync(
                    user,
                    new UserLoginInfo(ProviderName, identity.Subject, ProviderName));

                if (!linked.Succeeded)
                {
                    return Results.ValidationProblem(linked.Errors.ToDictionary(
                        e => e.Code, e => new[] { e.Description }));
                }
            }

            // Issues a bearer token rather than a cookie; the handler writes the
            // AccessTokenResponse body, which is why this returns Empty.
            signIn.AuthenticationScheme = IdentityConstants.BearerScheme;
            await signIn.SignInAsync(user, isPersistent: false);

            return Results.Empty;
        })
        .WithName("SignInWithGoogle")
        .WithSummary("Exchange a Google ID token for an Amicus access token.")
        .AllowAnonymous();

        return group;
    }
}
