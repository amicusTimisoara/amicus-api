namespace Amicus.Api.Setup;

public sealed class CorsOptions
{
    public const string SectionName = "Cors";

    /// <summary>
    /// Exact origins allowed to call the API from a browser, e.g.
    /// https://app.thorsp.net. The web app and the API are different origins now
    /// (app.thorsp.net calling api.thorsp.net), so the browser needs this.
    /// </summary>
    public string[] Origins { get; set; } = [];

    /// <summary>
    /// Origin suffixes allowed too, for Cloudflare Pages PR previews whose
    /// hostname is a per-branch hash, e.g. ".amicus-web.pages.dev".
    /// </summary>
    public string[] OriginSuffixes { get; set; } = [];
}

public static class CorsSetup
{
    public const string PolicyName = "amicus";

    public static IServiceCollection AddAmicusCors(
        this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(CorsOptions.SectionName).Get<CorsOptions>()
            ?? new CorsOptions();

        services.AddCors(cors => cors.AddPolicy(CorsSetup.PolicyName, policy => policy
            // Bearer tokens, not cookies — so no AllowCredentials; any origin on the
            // list may send an Authorization header and read the response.
            .SetIsOriginAllowed(origin =>
                options.Origins.Contains(origin, StringComparer.OrdinalIgnoreCase)
                || options.OriginSuffixes.Any(suffix =>
                    Uri.TryCreate(origin, UriKind.Absolute, out var u)
                    && u.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            .AllowAnyHeader()
            .AllowAnyMethod()));

        return services;
    }
}
