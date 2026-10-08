using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Desk.Data.Auth;

namespace Desk.Api.Auth;

/// <summary>Refuses sign-in for disabled or expired accounts (README §7.1). Identity reports these as <c>IsNotAllowed</c>.</summary>
public sealed class DeskSignInManager(
    UserManager<DeskUser> users,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<DeskUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<DeskUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<DeskUser> confirmation,
    TimeProvider time)
    : SignInManager<DeskUser>(users, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    public override async Task<bool> CanSignInAsync(DeskUser user) =>
        user.IsActiveAt(time.GetUtcNow()) && await base.CanSignInAsync(user);
}
