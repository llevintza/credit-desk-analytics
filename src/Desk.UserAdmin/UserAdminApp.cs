using System.Globalization;
using Desk.Data;
using Desk.Data.App;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Desk.UserAdmin;

/// <summary>
/// Invite-only account administration (README §7.1). A generated password is written to <c>stdout</c> exactly
/// once and never handed to a logger; only its hash is stored. Errors go to <c>stderr</c> and never echo a password.
/// </summary>
public sealed class UserAdminApp(TextWriter stdout, TextWriter stderr, ILoggerFactory? loggerFactory = null, TimeProvider? time = null)
{
    private readonly ILoggerFactory _loggers = loggerFactory ?? NullLoggerFactory.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Test seam: extra registrations applied after the defaults (e.g. a failing password validator).</summary>
    internal Action<IServiceCollection>? ConfigureServices { get; init; }

    public async Task<int> RunAsync(IReadOnlyList<string> args, IConfiguration config, CancellationToken ct)
    {
        UserAdminOptions options;
        try
        {
            options = UserAdminOptions.Parse(args);
        }
        catch (ArgumentException e)
        {
            await stderr.WriteLineAsync($"ERROR: {e.Message}\n\n{UserAdminOptions.Usage}");
            return 1;
        }

        try
        {
            ConnectionStrings.Resolve(config, ConnectionStrings.App);
        }
        catch (InvalidOperationException e)
        {
            await stderr.WriteLineAsync($"ERROR: {e.Message}");
            return 1;
        }

        await using var services = BuildServices(config);
        await using var scope = services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<DeskUser>>();
        try
        {
            // UserManager takes no token, so check before each database step.
            ct.ThrowIfCancellationRequested();
            return options.Command switch
            {
                UserAdminCommand.Add => await AddAsync(users, options, ct),
                UserAdminCommand.List => await ListAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), ct),
                UserAdminCommand.Disable => await DisableAsync(users, options.Email!, ct),
                _ => await ResetAsync(users, options.Email!, ct),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await stderr.WriteLineAsync("ERROR: cancelled.");
            return 130;
        }
        catch (Exception e)
        {
            // Type and message only: Npgsql messages name the host but never carry the password.
            await stderr.WriteLineAsync($"ERROR: {e.GetType().Name}: {e.Message}");
            return 3;
        }
    }

    private ServiceProvider BuildServices(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_loggers);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(_time);
        services.AddDeskData(config);
        services.AddIdentityCore<DeskUser>(IdentityPolicy.Apply)
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>();
        ConfigureServices?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private async Task<int> AddAsync(UserManager<DeskUser> users, UserAdminOptions o, CancellationToken ct)
    {
        var expiresAt = UserAdminOptions.ExpiryInstant(o.Expires!.Value);
        if (expiresAt <= _time.GetUtcNow())
            return await FailAsync($"--expires {o.Expires:yyyy-MM-dd} is in the past.");
        if (await users.FindByEmailAsync(o.Email!) is not null)
            return await FailAsync($"An account for {o.Email} already exists. Use reset to issue a new password.");

        ct.ThrowIfCancellationRequested();
        var password = PasswordGenerator.Generate();
        var user = new DeskUser { UserName = o.Email, Email = o.Email, EmailConfirmed = true, ExpiresAt = expiresAt };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
            return await FailAsync(Describe(created));
        ct.ThrowIfCancellationRequested();
        Check(await users.AddToRoleAsync(user, o.Role!));

        await stdout.WriteLineAsync($"Created {o.Role} account {o.Email}, valid through {o.Expires:yyyy-MM-dd} (UTC).");
        await PrintPasswordOnceAsync(password);
        return 0;
    }

    private async Task<int> ListAsync(AppDbContext db, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        // One round trip, only the columns shown (no password hashes or stamps leave the database).
        var all = await db.Users.AsNoTracking()
            .OrderBy(u => u.Email)
            .Select(u => new
            {
                u.Email,
                u.ExpiresAt,
                u.IsDisabled,
                u.LockoutEnd,
                Roles = db.UserRoles.Where(ur => ur.UserId == u.Id)
                    .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name)
                    .OrderBy(n => n)
                    .ToList(),
            })
            .ToListAsync(ct);
        await stdout.WriteLineAsync($"{"EMAIL",-40} {"ROLES",-14} {"EXPIRES (UTC)",-17} STATUS");
        foreach (var u in all)
        {
            var roleNames = string.Join(",", u.Roles);
            var status = u.IsDisabled ? "disabled"
                : now >= u.ExpiresAt ? "expired"
                : u.LockoutEnd > now ? "locked"
                : "active";
            await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{u.Email,-40} {roleNames,-14} {u.ExpiresAt.AddDays(-1):yyyy-MM-dd}        {status}"));
        }
        await stdout.WriteLineAsync($"{all.Count} account(s).");
        return 0;
    }

    private async Task<int> DisableAsync(UserManager<DeskUser> users, string email, CancellationToken ct)
    {
        if (await users.FindByEmailAsync(email) is not { } user)
            return await FailAsync($"No account for {email}.");
        ct.ThrowIfCancellationRequested();
        user.IsDisabled = true;
        // One write: the stamp update saves the whole user, flag included. The new stamp makes the API's
        // security-stamp check end any live session (README §7.1).
        Check(await users.UpdateSecurityStampAsync(user));
        await stdout.WriteLineAsync($"Disabled {email}. Live sessions end within a few minutes.");
        return 0;
    }

    private async Task<int> ResetAsync(UserManager<DeskUser> users, string email, CancellationToken ct)
    {
        if (await users.FindByEmailAsync(email) is not { } user)
            return await FailAsync($"No account for {email}.");
        ct.ThrowIfCancellationRequested();
        var password = PasswordGenerator.Generate();
        // Validate first, then one write (#314): separate remove/add writes could leave the account with no
        // password hash if the add failed. A rejected password throws before anything is saved.
        // Every validator runs and all reasons are reported, as UserManager.ValidatePasswordAsync does.
        var errors = new List<IdentityError>();
        foreach (var validator in users.PasswordValidators)
            errors.AddRange((await validator.ValidateAsync(users, user, password)).Errors);
        Check(errors.Count == 0 ? IdentityResult.Success : IdentityResult.Failed([.. errors]));
        ct.ThrowIfCancellationRequested();
        user.PasswordHash = users.PasswordHasher.HashPassword(user, password);
        user.LockoutEnd = null;
        user.AccessFailedCount = 0;
        // The stamp update saves the whole user, as in DisableAsync; the new stamp ends live sessions.
        Check(await users.UpdateSecurityStampAsync(user));

        await stdout.WriteLineAsync($"Reset the password for {email} and cleared any lockout.");
        await PrintPasswordOnceAsync(password);
        return 0;
    }

    private async Task PrintPasswordOnceAsync(string password)
    {
        await stdout.WriteLineAsync($"Password (shown once, share it out of band): {password}");
        await stdout.FlushAsync();
    }

    private async Task<int> FailAsync(string message)
    {
        await stderr.WriteLineAsync($"ERROR: {message}");
        return 1;
    }

    internal static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(Describe(result));
    }

    private static string Describe(IdentityResult result) => string.Join(" ", result.Errors.Select(e => e.Description));
}
