using Microsoft.AspNetCore.DataProtection;
using Amicus.Infrastructure;
using Amicus.Infrastructure.Identity;

namespace Amicus.Api.Setup;

/// <summary>
/// Identity wiring lives in the API rather than in Infrastructure on purpose:
/// <c>AddIdentityApiEndpoints</c> is part of the ASP.NET Core shared framework, and
/// pulling that into a class library would make the persistence layer depend on the
/// web stack.
/// </summary>
public static class IdentitySetup
{
    /// <summary>
    /// Persists Data Protection keys to disk when a path is configured.
    ///
    /// Identity's bearer tokens are protected with Data Protection, whose keys live
    /// in memory unless told otherwise. On a service that restarts — a deploy, a
    /// reboot, a crash — that silently invalidates every token in the wild and
    /// every signed-in student is bounced to the login screen for no visible reason.
    /// </summary>
    public static IServiceCollection AddAmicusDataProtection(
        this IServiceCollection services, IConfiguration configuration)
    {
        var keyPath = configuration["DataProtection:KeyPath"];

        var protection = services.AddDataProtection()
            // Pinned rather than derived from the content root: the default is the
            // application's path, so moving or renaming the deploy directory would
            // orphan the keys and have the same effect as losing them.
            .SetApplicationName("amicus-api");

        if (!string.IsNullOrWhiteSpace(keyPath))
        {
            protection.PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        }

        return services;
    }

    public static IServiceCollection AddAmicusIdentity(this IServiceCollection services)
    {
        services.AddAuthorization();

        services
            .AddIdentityApiEndpoints<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedEmail = false;

                // Students type these on a phone. Length carries far more real
                // strength than symbol classes, which mostly produce "Pa$$w0rd".
                options.Password.RequiredLength = 10;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireDigit = false;

                options.Lockout.MaxFailedAccessAttempts = 10;
            })
            .AddRoles<AppRole>()
            .AddEntityFrameworkStores<AmicusDbContext>()
            .AddUserManager<AutoConfirmUserManager>();

        return services;
    }
}
