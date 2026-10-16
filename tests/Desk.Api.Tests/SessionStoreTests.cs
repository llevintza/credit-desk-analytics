using System.Net;
using System.Net.Http.Json;
using Desk.Api.Auth;
using Desk.Data.App;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Desk.Api.Tests;

/// <summary>
/// #354 (ADR-0024): logout revocations and the refresh rotation live in <c>auth.sessions</c>, so a restart or a second
/// instance knows both. The store's rules (#353) are tested on the real table; the host tests use two hosts on one
/// database (two instances, or one before and after a restart: each host has its own memory).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class SessionStoreTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    private DateTimeOffset Now => api.Time.GetUtcNow();

    private (SessionStore Store, SessionRevocations Revoked) NewInstance()
    {
        var revoked = new SessionRevocations();
        return (new SessionStore(Grace, api.Services.GetRequiredService<IDbContextFactory<AppDbContext>>(), revoked), revoked);
    }

    private static string Sid() => Guid.NewGuid().ToString("N");

    private async Task<AuthSession?> RowAsync(string sid)
    {
        await using var db = api.NewContext();
        return await db.AuthSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Sid == sid, Ct);
    }

    [Fact]
    public async Task Each_id_swaps_once_and_a_token_two_swaps_old_ends_the_session()
    {
        var (store, revoked) = NewInstance();
        var (now, sid) = (Now, Sid());
        var end = now.AddHours(24);
        await store.IssueAsync(sid, "r0", end, now, Ct);

        Assert.Equal(new Rotation("r1", false), await store.RotateAsync(sid, "r0", "r1", end, now, Ct));
        Assert.Equal(new Rotation("r2", false), await store.RotateAsync(sid, "r1", "r2", end, now, Ct));
        Assert.Equal(new Rotation(null, true), await store.RotateAsync(sid, "r0", "r3", end, now, Ct)); // even within the window

        // The replay ended the session: in the table for every instance, and in this one's memory.
        Assert.NotNull((await RowAsync(sid))!.RevokedAt);
        Assert.True(revoked.IsRevoked(sid, now));
        Assert.Equal(new Rotation(null, false), await store.RotateAsync(sid, "r2", "r4", end, now, Ct));
    }

    [Fact]
    public async Task A_token_without_an_id_is_a_replay_once_a_chain_is_known()
    {
        var (store, _) = NewInstance();
        var (now, sid) = (Now, Sid());
        await store.IssueAsync(sid, "r0", now.AddHours(24), now, Ct);
        Assert.Equal(new Rotation(null, true), await store.RotateAsync(sid, null, "r1", now.AddHours(24), now, Ct));
    }

    [Fact]
    public async Task A_session_without_a_row_is_believed_once_then_single_use()
    {
        // A token from before #354, or one whose row was pruned: its first refresh starts the chain in the table.
        var (store, _) = NewInstance();
        var (now, sid) = (Now, Sid());
        var end = now.AddHours(24);
        Assert.Equal(new Rotation("q1", false), await store.RotateAsync(sid, null, "q1", end, now, Ct));
        var row = (await RowAsync(sid))!;
        Assert.Equal(("q1", (string?)null, end), (row.Rid, row.PreviousRid, row.ExpiresAt));
        Assert.Equal(new Rotation(null, true), await store.RotateAsync(sid, null, "q2", end, now, Ct));

        // One that carried an id: that id is the previous one, with the same window.
        var other = Sid();
        Assert.Equal(new Rotation("p1", false), await store.RotateAsync(other, "p0", "p1", end, now, Ct));
        Assert.Equal(new Rotation("p1", false), await store.RotateAsync(other, "p0", "p2", end, now.AddSeconds(1), Ct));
    }

    [Fact]
    public async Task The_token_swapped_last_gets_the_current_id_back_within_the_grace_window_only()
    {
        var (store, _) = NewInstance();
        var (now, sid) = (Now, Sid());
        var end = now.AddHours(24);
        await store.IssueAsync(sid, "r0", end, now, Ct);
        Assert.Equal("r1", (await store.RotateAsync(sid, "r0", "r1", end, now, Ct)).Rid);

        // r0 again (a reload aborted the refresh that swapped it): the current id, and no new swap.
        Assert.Equal("r1", (await store.RotateAsync(sid, "r0", "x", end, now.AddSeconds(29), Ct)).Rid);
        // The table keeps microseconds: the last instant inside the window, not the last tick.
        Assert.Equal("r1", (await store.RotateAsync(sid, "r0", "y", end, now + Grace - TimeSpan.FromMicroseconds(1), Ct)).Rid);
        Assert.Equal("r1", (await RowAsync(sid))!.Rid);

        // The window runs from the last swap, and covers only the id that swap replaced.
        Assert.Equal("r2", (await store.RotateAsync(sid, "r1", "r2", end, now.AddMinutes(5), Ct)).Rid);
        Assert.Equal("r2", (await store.RotateAsync(sid, "r1", "w", end, now.AddMinutes(5).AddSeconds(10), Ct)).Rid);

        // At the window's end the previous token is a replay.
        Assert.Equal(new Rotation(null, true), await store.RotateAsync(sid, "r1", "z", end, now.AddMinutes(5) + Grace, Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // a session with no row yet (a token from before #354)
    public async Task Concurrent_refreshes_on_two_instances_swap_once_and_all_get_the_same_id(bool known)
    {
        var instances = new[] { NewInstance().Store, NewInstance().Store };
        var now = Now;
        var end = now.AddHours(24);
        const int racers = 4;
        // Many short races, each released at once, so the lost conditional UPDATE (and INSERT) is taken too.
        for (var round = 0; round < 25; round++)
        {
            var sid = Sid();
            if (known) await instances[0].IssueAsync(sid, "r0", end, now, Ct);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var racing = Enumerable.Range(0, racers).Select(i => Task.Run(async () =>
            {
                await start.Task;
                return await instances[i % 2].RotateAsync(sid, "r0", $"n{i}", end, now, Ct);
            }, Ct)).ToArray();
            start.SetResult();
            var results = await Task.WhenAll(racing);

            Assert.All(results, r => Assert.False(r.Replayed));
            var winner = Assert.Single(results.Select(r => r.Rid).Distinct());
            Assert.NotNull(winner);
            Assert.Equal(winner, (await instances[1].RotateAsync(sid, "r0", "late", end, now, Ct)).Rid);
        }
    }

    [Fact]
    public async Task A_logout_of_a_session_without_a_row_is_stored_and_a_later_refresh_is_refused_without_a_replay()
    {
        var (first, _) = NewInstance();
        var (second, secondRevoked) = NewInstance();
        var (now, sid) = (Now, Sid());
        var end = now.AddHours(24);
        await first.RevokeAsync(sid, end, now);
        await first.RevokeAsync(sid, end, now.AddMinutes(1)); // again: keeps the first time

        var row = (await RowAsync(sid))!;
        Assert.Equal(now, row.RevokedAt);
        Assert.Null(row.Rid);

        Assert.False(secondRevoked.IsRevoked(sid, now));
        Assert.Equal(new Rotation(null, false), await second.RotateAsync(sid, "r0", "r1", end, now, Ct));
        Assert.True(secondRevoked.IsRevoked(sid, now)); // learned: this instance now refuses its access tokens too
    }

    [Fact]
    public async Task A_sign_in_prunes_sessions_past_their_limit_and_a_past_limit_is_never_stored()
    {
        var (store, _) = NewInstance();
        var (now, old, past) = (Now, Sid(), Sid());
        await store.IssueAsync(old, "r0", now.AddHours(1), now, Ct);
        await store.RevokeAsync(past, now, now); // already at its limit: nothing to remember
        Assert.Null(await RowAsync(past));

        var fresh = Sid();
        await store.IssueAsync(fresh, "r0", now.AddHours(26), now.AddHours(2), Ct);
        Assert.Null(await RowAsync(old));
        Assert.NotNull(await RowAsync(fresh));
    }

    // ---- through the API: two hosts on one database ----

    private static async Task<AccessTokenResponse> RefreshAsync(HttpClient client, string refreshToken)
    {
        var res = await PostgresApiFactory.PostRefreshAsync(client, refreshToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<AccessTokenResponse>(Ct))!;
    }

    private static HttpClient ClientWith(WebApplicationFactory<Program> host, string accessToken)
    {
        var client = PostgresApiFactory.NewClient(host);
        PostgresApiFactory.UseToken(client, accessToken);
        return client;
    }

    [Fact]
    public async Task A_logout_on_one_instance_ends_the_refresh_token_on_another_and_after_a_restart()
    {
        await using var first = api.WithSettings();
        var client = PostgresApiFactory.NewClient(first);
        var tokens = await PostgresApiFactory.LoginAsync(client, (await api.CreateUserAsync()).Email!);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null, Ct)).StatusCode);

        await using var second = api.WithSettings(); // another instance, or this one restarted: nothing in memory
        var elsewhere = ClientWith(second, tokens.AccessToken);
        // The documented limit (ADR-0024): an access token this instance hasn't seen revoked lives out its 5 minutes.
        Assert.Equal(HttpStatusCode.OK, (await elsewhere.GetAsync("/api/me", Ct)).StatusCode);
        // Its refresh token is refused everywhere (before #354, this instance would have issued new tokens for 8 h).
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostRefreshAsync(elsewhere, tokens.RefreshToken)).StatusCode);
        // And the refused refresh taught this instance: the access token stops working here too.
        Assert.Equal(HttpStatusCode.Unauthorized, (await elsewhere.GetAsync("/api/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_refresh_rotation_is_shared_between_instances_and_survives_a_restart()
    {
        var (first, clock) = api.WithOwnClock();
        await using var _ = first;
        await using var second = first.WithWebHostBuilder(_ => { }); // same clock, own memory
        var client = PostgresApiFactory.NewClient(first);
        var t0 = await PostgresApiFactory.LoginAsync(client, (await api.CreateUserAsync()).Email!);

        var t1 = await RefreshAsync(PostgresApiFactory.NewClient(first), t0.RefreshToken);
        var t2 = await RefreshAsync(PostgresApiFactory.NewClient(second), t1.RefreshToken); // the swap on the first is known here

        // t1 back on the first instance once the window is over: a replay, although that instance swapped to t1 itself.
        clock.Advance(SessionStore.DefaultGrace);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostRefreshAsync(PostgresApiFactory.NewClient(first), t1.RefreshToken)).StatusCode);

        // The session is over everywhere: the second instance refuses the current token, and so does a restarted one.
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostRefreshAsync(PostgresApiFactory.NewClient(second), t2.RefreshToken)).StatusCode);
        await using var restarted = first.WithWebHostBuilder(_ => { });
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostRefreshAsync(PostgresApiFactory.NewClient(restarted), t2.RefreshToken)).StatusCode);
    }
}
