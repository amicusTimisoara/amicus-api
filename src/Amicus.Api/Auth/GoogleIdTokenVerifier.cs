using Google.Apis.Auth;
using Microsoft.Extensions.Options;

namespace Amicus.Api.Auth;

public sealed class GoogleAuthOptions
{
    public const string SectionName = "Authentication:Google";

    /// <summary>
    /// Every OAuth client ID allowed to mint tokens for us — web, iOS and Android
    /// are separate clients in Google Cloud but one account here, so the audience
    /// check has to accept all of them.
    /// </summary>
    public string[] ClientIds { get; set; } = [];
}

public sealed class GoogleIdTokenVerifier(
    IOptions<GoogleAuthOptions> options,
    ILogger<GoogleIdTokenVerifier> logger) : IGoogleIdTokenVerifier
{
    private readonly GoogleAuthOptions _options = options.Value;

    public async Task<GoogleIdentity?> VerifyAsync(
        string idToken, CancellationToken cancellationToken = default)
    {
        if (_options.ClientIds.Length == 0)
        {
            throw new InvalidOperationException(
                $"{GoogleAuthOptions.SectionName}:ClientIds is not configured.");
        }

        GoogleJsonWebSignature.Payload payload;

        try
        {
            // Checks the signature against Google's rotating JWKS, the issuer, the
            // expiry, AND that the audience is one of ours — the last part is what
            // stops a token minted for some other app being replayed here.
            payload = await GoogleJsonWebSignature.ValidateAsync(
                idToken,
                new GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = _options.ClientIds,
                });
        }
        catch (InvalidJwtException ex)
        {
            // Expected for anything expired, forged, or aimed at another audience.
            // Logged at Information because it is a client error, not our fault.
            logger.LogInformation("Rejected a Google ID token: {Reason}", ex.Message);
            return null;
        }

        if (string.IsNullOrWhiteSpace(payload.Subject) || string.IsNullOrWhiteSpace(payload.Email))
        {
            logger.LogWarning("Google ID token verified but carried no subject or email.");
            return null;
        }

        return new GoogleIdentity(
            payload.Subject,
            payload.Email,
            payload.EmailVerified,
            payload.Name,
            payload.Picture);
    }
}
