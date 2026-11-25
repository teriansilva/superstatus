using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SuperStatus.Services.Updates;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #311: the in-app update trigger calls Watchtower's http-api. These cover the
/// outcomes the panel renders — accepted (202), not-configured (no URL/token),
/// unauthorized (token mismatch), unreachable (timeout/connection), and the anti-spam
/// cooldown — and that the bearer token is sent but never surfaced in messages.
/// </summary>
[TestClass]
public class UpdateTriggerTests
{
    private const string Url = "http://watchtower:8080/v1/update";
    private const string Token = "shared-secret-token";

    private static WatchtowerUpdateTrigger Trigger(HttpMessageHandler handler, string? url = Url, string? token = Token)
    {
        var factory = new SingleClientFactory(new HttpClient(handler));
        return new WatchtowerUpdateTrigger(factory, new UpdateTriggerOptions(url, token), NullLogger<WatchtowerUpdateTrigger>.Instance);
    }

    [TestMethod]
    public void CanApply_falseWhenUrlOrTokenMissing()
    {
        Assert.IsFalse(Trigger(new StubHandler(HttpStatusCode.OK), url: null).CanApply);
        Assert.IsFalse(Trigger(new StubHandler(HttpStatusCode.OK), token: " ").CanApply);
        Assert.IsTrue(Trigger(new StubHandler(HttpStatusCode.OK)).CanApply);
    }

    [TestMethod]
    public async Task NotConfigured_returnsNotConfigured_withoutCalling()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var result = await Trigger(handler, token: null).TriggerAsync();
        Assert.AreEqual(UpdateTriggerOutcome.NotConfigured, result.Outcome);
        Assert.AreEqual(0, handler.Calls, "no HTTP call when not configured");
    }

    [TestMethod]
    public async Task Accepted_onSuccess_sendsBearerToken()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var result = await Trigger(handler).TriggerAsync();
        Assert.AreEqual(UpdateTriggerOutcome.Accepted, result.Outcome);
        Assert.IsTrue(result.Accepted);
        Assert.AreEqual("Bearer", handler.LastAuthScheme);
        Assert.AreEqual(Token, handler.LastAuthParameter);
    }

    [TestMethod]
    public async Task Unauthorized_onTokenReject_andMessageOmitsToken()
    {
        var result = await Trigger(new StubHandler(HttpStatusCode.Unauthorized)).TriggerAsync();
        Assert.AreEqual(UpdateTriggerOutcome.Unauthorized, result.Outcome);
        Assert.IsFalse(result.Accepted);
        StringAssert.DoesNotMatch(result.Error ?? "", new System.Text.RegularExpressions.Regex(Token));
    }

    [TestMethod]
    public async Task Unreachable_onTransportFailure()
    {
        var result = await Trigger(new ThrowingHandler()).TriggerAsync();
        Assert.AreEqual(UpdateTriggerOutcome.Unreachable, result.Outcome);
        Assert.IsFalse(result.Accepted);
    }

    [TestMethod]
    public async Task TooSoon_onSecondTriggerWithinCooldown()
    {
        var trigger = Trigger(new StubHandler(HttpStatusCode.OK));
        var first = await trigger.TriggerAsync();
        var second = await trigger.TriggerAsync();
        Assert.AreEqual(UpdateTriggerOutcome.Accepted, first.Outcome);
        Assert.AreEqual(UpdateTriggerOutcome.TooSoon, second.Outcome, "anti-spam: a second click within the cooldown is refused");
    }

    [TestMethod]
    public async Task FailedTrigger_doesNotHoldCooldown()
    {
        // A rejected/unreachable attempt should allow an immediate retry after a fix.
        var handler = new StubHandler(HttpStatusCode.Unauthorized);
        var trigger = Trigger(handler);
        await trigger.TriggerAsync();
        var retry = await trigger.TriggerAsync();
        Assert.AreNotEqual(UpdateTriggerOutcome.TooSoon, retry.Outcome, "a failed attempt must not lock out a retry");
    }

    private sealed class StubHandler(HttpStatusCode code) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastAuthScheme { get; private set; }
        public string? LastAuthParameter { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastAuthScheme = request.Headers.Authorization?.Scheme;
            LastAuthParameter = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(code));
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
