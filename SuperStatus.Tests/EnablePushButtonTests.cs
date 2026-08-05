using System.Net;
using System.Text;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using SuperStatus.Web;
using SuperStatus.Web.Components.Admin;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// Issue #241 Phase C2 — the "Allow push on this browser" control. Probes
/// the browser (mocked JS interop) on render and drives the enable flow through to a
/// server subscribe POST.
/// </summary>
[TestClass]
public class EnablePushButtonTests
{
    private static (BunitTestContext ctx, List<string> posts) Make(
        bool supported, string permission = "default", bool subscribed = false)
    {
        var ctx = new BunitTestContext();
        var posts = new List<string>();
        ctx.Services.AddSingleton(new PushApiClient(
            new HttpClient(new Handler(posts)) { BaseAddress = new Uri("http://api.test") }));
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<bool>("superPush.isSupported").SetResult(supported);
        ctx.JSInterop.Setup<string>("superPush.permission").SetResult(permission);
        ctx.JSInterop.Setup<bool>("superPush.isSubscribed").SetResult(subscribed);
        return (ctx, posts);
    }

    [TestMethod]
    public void Unsupported_showsHint_andNoButton()
    {
        var (ctx, _) = Make(supported: false); using var _c = ctx;
        var cut = ctx.RenderComponent<EnablePushButton>();
        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Markup.Contains("doesn't support web push"));
            Assert.AreEqual(0, cut.FindAll("button").Count, "no actionable button when unsupported");
        });
    }

    [TestMethod]
    public void Supported_notSubscribed_showsEnable()
    {
        var (ctx, _) = Make(supported: true, subscribed: false); using var _c = ctx;
        var cut = ctx.RenderComponent<EnablePushButton>();
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Allow push on this browser")));
    }

    [TestMethod]
    public void Supported_alreadySubscribed_showsDisable()
    {
        var (ctx, _) = Make(supported: true, subscribed: true); using var _c = ctx;
        var cut = ctx.RenderComponent<EnablePushButton>();
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Disable on this device")));
    }

    [TestMethod]
    public void PermissionDenied_showsBlockedHint()
    {
        var (ctx, _) = Make(supported: true, permission: "denied"); using var _c = ctx;
        var cut = ctx.RenderComponent<EnablePushButton>();
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("blocked")));
    }

    [TestMethod]
    public void Enable_subscribesBrowser_thenPostsSubscriptionToServer()
    {
        var (ctx, posts) = Make(supported: true, subscribed: false); using var _c = ctx;
        // The browser-side subscribe returns a subscription for any VAPID key.
        ctx.JSInterop.Setup<EnablePushButton.PushSub?>("superPush.subscribe", _ => true)
           .SetResult(new EnablePushButton.PushSub
           {
               Endpoint = "https://push.test/abc",
               P256dh = "p256",
               Auth = "auth",
               UserAgent = "UnitTest",
           });

        var cut = ctx.RenderComponent<EnablePushButton>();
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Allow push on this browser")));

        cut.Find("button").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(posts.Contains("/api/push/subscribe"), "the subscription is sent to the server");
            Assert.IsTrue(cut.Markup.Contains("Disable on this device"), "the control flips to the enabled state");
        });
    }

    [TestMethod]
    public void Enable_serverPostFails_rollsBackBrowserSubscription_andStaysDisabled()
    {
        // Regression (Hermes #265): if the browser subscribes but the server POST
        // fails, the local subscription must be rolled back — otherwise the device
        // looks enabled (isSubscribed() == true) while the server can't push to it.
        var ctx = new BunitTestContext();
        var posts = new List<string>();
        ctx.Services.AddSingleton(new PushApiClient(
            new HttpClient(new FailingSubscribeHandler(posts)) { BaseAddress = new Uri("http://api.test") }));
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.JSInterop.Setup<bool>("superPush.isSupported").SetResult(true);
        ctx.JSInterop.Setup<string>("superPush.permission").SetResult("default");
        ctx.JSInterop.Setup<bool>("superPush.isSubscribed").SetResult(false);
        ctx.JSInterop.Setup<EnablePushButton.PushSub?>("superPush.subscribe", _ => true)
           .SetResult(new EnablePushButton.PushSub
           {
               Endpoint = "https://push.test/abc",
               P256dh = "p256",
               Auth = "auth",
               UserAgent = "UnitTest",
           });
        using var _c = ctx;

        var cut = ctx.RenderComponent<EnablePushButton>();
        cut.WaitForAssertion(() => Assert.IsTrue(cut.Markup.Contains("Allow push on this browser")));

        cut.Find("button").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(posts.Contains("/api/push/subscribe"), "the server POST was attempted");
            // On its failure the browser subscription is rolled back...
            ctx.JSInterop.VerifyInvoke("superPush.unsubscribe");
            // ...and the control stays in the enable state so the operator can retry.
            Assert.IsTrue(cut.Markup.Contains("Allow push on this browser"));
            Assert.IsFalse(cut.Markup.Contains("Disable on this device"));
        });
    }

    private sealed class Handler(List<string> posts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post) posts.Add(path);
            // GET /api/push/vapid-key returns a key; the subscribe/unsubscribe POSTs return ok.
            var body = path.EndsWith("/vapid-key") ? "{\"key\":\"BPtestVapidKey\"}" : "{\"ok\":true}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    // Like Handler, but the /api/push/subscribe POST fails (500) — to exercise the
    // server-registration-failed rollback path.
    private sealed class FailingSubscribeHandler(List<string> posts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post) posts.Add(path);
            if (path.EndsWith("/subscribe"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            var body = path.EndsWith("/vapid-key") ? "{\"key\":\"BPtestVapidKey\"}" : "{\"ok\":true}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
