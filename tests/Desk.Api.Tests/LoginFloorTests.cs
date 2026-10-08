using System.Text;
using Desk.Api.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;

namespace Desk.Api.Tests;

/// <summary>#230: <see cref="LoginFloor"/> on its own, on a fake clock. The end-to-end paths are in <see cref="LoginTimingTests"/>.</summary>
public sealed class LoginFloorTests
{
    private static readonly LoginFloorOptions Options = LoginFloorOptions.Default;
    private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));

    private static readonly Endpoint LoginEndpoint = new(null, new EndpointMetadataCollection(LoginFloorMetadata.Instance), "Login");
    private static readonly Endpoint OtherEndpoint = new(null, new EndpointMetadataCollection(), "Other");

    /// <summary>A request routed to <paramref name="endpoint"/> (the login endpoint by default; null: unrouted).</summary>
    private static DefaultHttpContext Request(Endpoint? endpoint)
    {
        var http = new DefaultHttpContext();
        http.SetEndpoint(endpoint);
        http.Response.Body = new MemoryStream();
        return http;
    }

    private static DefaultHttpContext Login() => Request(LoginEndpoint);

    private static RequestDelegate Answer(int status, Action? during = null) => async http =>
    {
        during?.Invoke();
        http.Response.StatusCode = status;
        await http.Response.WriteAsync("answer", TestContext.Current.CancellationToken);
    };

    private static string Body(HttpContext http) => Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray());

    [Fact]
    public async Task A_401_is_held_back_until_the_floor_and_then_sent_whole()
    {
        var http = Login();
        var pending = new LoginFloor(Answer(StatusCodes.Status401Unauthorized), Options, _time).InvokeAsync(http);

        _time.Advance(Options.Floor - Tick);
        Assert.False(pending.IsCompleted);
        Assert.Equal("", Body(http));

        _time.Advance(Options.MaxJitter + Tick);
        await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("answer", Body(http));
    }

    [Fact]
    public async Task A_401_that_already_took_longer_than_the_floor_is_not_delayed_further()
    {
        var http = Login();
        var slow = Answer(StatusCodes.Status401Unauthorized, () => _time.Advance(Options.Floor + Options.MaxJitter));

        var pending = new LoginFloor(slow, Options, _time).InvokeAsync(http);

        Assert.True(pending.IsCompleted);
        await pending;
        Assert.Equal("answer", Body(http));
    }

    [Theory]
    [InlineData(StatusCodes.Status200OK)]
    [InlineData(StatusCodes.Status429TooManyRequests)]
    [InlineData(StatusCodes.Status400BadRequest)]
    public async Task A_login_that_is_not_a_401_answers_at_once(int status)
    {
        var http = Login();

        var pending = new LoginFloor(Answer(status), Options, _time).InvokeAsync(http);

        Assert.True(pending.IsCompleted);
        await pending;
        Assert.Equal("answer", Body(http));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_401_from_any_other_endpoint_answers_at_once(bool routed)
    {
        // Selected by the endpoint's marker, never by the path: /api/me is a 401 for everyone signed out.
        var http = Request(routed ? OtherEndpoint : null);

        var pending = new LoginFloor(Answer(StatusCodes.Status401Unauthorized), Options, _time).InvokeAsync(http);

        Assert.True(pending.IsCompleted);
        await pending;
        Assert.Equal("answer", Body(http));
    }

    [Fact]
    public async Task Only_the_login_response_is_buffered()
    {
        var http = Request(OtherEndpoint);
        var original = http.Response.Body;
        Stream? seen = null;

        await new LoginFloor(h => { seen = h.Response.Body; return Task.CompletedTask; }, Options, _time).InvokeAsync(http);

        Assert.Same(original, seen);
    }

    [Fact]
    public async Task A_client_that_goes_away_ends_the_wait_and_gets_nothing()
    {
        using var aborted = new CancellationTokenSource();
        var http = Login();
        http.RequestAborted = aborted.Token;
        var pending = new LoginFloor(Answer(StatusCodes.Status401Unauthorized), Options, _time).InvokeAsync(http);
        Assert.False(pending.IsCompleted);

        await aborted.CancelAsync();

        // Bounded: a wait that ignores the client times out (TimeoutException) instead of hanging the run.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal("", Body(http));
    }

    [Fact]
    public async Task A_failing_login_handler_gets_the_real_response_back_for_the_error_page()
    {
        var http = Login();
        var original = http.Response.Body;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LoginFloor(_ => throw new InvalidOperationException("boom"), Options, _time).InvokeAsync(http));

        Assert.Same(original, http.Response.Body);
    }
}
