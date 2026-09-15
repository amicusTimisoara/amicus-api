using Amicus.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Amicus.Api.Setup;

/// <summary>
/// Marks every new account's email confirmed on creation.
///
/// The app does not require email confirmation to sign in
/// (<c>SignIn.RequireConfirmedEmail = false</c>), but Identity's
/// <c>/forgotPassword</c> still only sends a reset to a user whose email IS
/// confirmed — so without this, password reset silently does nothing for exactly
/// the people who registered with a password. Confirming on creation closes that
/// gap and matches the Google path, which already sets it. The admin reset stays
/// as the fallback for any account that predates this.
/// </summary>
public sealed class AutoConfirmUserManager(
    IUserStore<AppUser> store,
    IOptions<IdentityOptions> optionsAccessor,
    IPasswordHasher<AppUser> passwordHasher,
    IEnumerable<IUserValidator<AppUser>> userValidators,
    IEnumerable<IPasswordValidator<AppUser>> passwordValidators,
    ILookupNormalizer keyNormalizer,
    IdentityErrorDescriber errors,
    IServiceProvider services,
    ILogger<UserManager<AppUser>> logger)
    : UserManager<AppUser>(store, optionsAccessor, passwordHasher, userValidators,
        passwordValidators, keyNormalizer, errors, services, logger)
{
    public override Task<IdentityResult> CreateAsync(AppUser user, string password)
    {
        user.EmailConfirmed = true;
        return base.CreateAsync(user, password);
    }

    public override Task<IdentityResult> CreateAsync(AppUser user)
    {
        user.EmailConfirmed = true;
        return base.CreateAsync(user);
    }
}
