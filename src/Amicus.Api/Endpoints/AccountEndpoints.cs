using System.Security.Claims;
using Amicus.Api.Contracts;
using Amicus.Infrastructure;
using Amicus.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Amicus.Api.Endpoints;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccount(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/account").RequireAuthorization();

        // Identity's /auth/manage/info returns only email + isEmailConfirmed, so it
        // can't carry the display name or photo the clients want. This does.
        group.MapGet("/me", async (
            UserManager<AppUser> users, AmicusDbContext db,
            ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null)
            {
                return Results.NotFound();
            }

            var isCarte = await db.Specialists
                .AnyAsync(s => s.UserId == user.Id && s.IsActive, ct);

            return Results.Ok(new AccountInfo(
                user.Email!, user.DisplayName, user.PhotoUrl, user.EmailConfirmed, isCarte));
        })
            .WithSummary("The signed-in user's own profile (email, display name, photo).");

        group.MapPatch("/me", async (
            [FromBody] UpdateAccountRequest request,
            UserManager<AppUser> users, AmicusDbContext db,
            ClaimsPrincipal principal, CancellationToken ct) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null)
            {
                return Results.NotFound();
            }

            if (request.DisplayName is not null)
            {
                var name = request.DisplayName.Trim();
                user.DisplayName = string.IsNullOrEmpty(name) ? null : name;
                var result = await users.UpdateAsync(user);
                if (!result.Succeeded)
                {
                    return Results.ValidationProblem(result.Errors.ToDictionary(
                        e => e.Code, e => new[] { e.Description }));
                }
            }

            var isCarte = await db.Specialists
                .AnyAsync(s => s.UserId == user.Id && s.IsActive, ct);

            return Results.Ok(new AccountInfo(
                user.Email!, user.DisplayName, user.PhotoUrl, user.EmailConfirmed, isCarte));
        })
            .WithSummary("Update the signed-in user's own profile (display name).");

        return app;
    }
}
