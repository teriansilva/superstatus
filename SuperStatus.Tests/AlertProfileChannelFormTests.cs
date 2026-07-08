using System.Net;
using System.Text;
using Bunit;
using Bunit.TestDoubles;
using MudBlazor;
using MudBlazor.Services;
using SuperStatus.Web;
using SuperStatus.Web.Components.Admin;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

/// <summary>
/// #343 Phase 5: the generic schema-driven channel form on the alert-profile editor renders
/// a channel's ConfigSchema — the Slack channel checkbox appears, and enabling it reveals its
/// (secret) webhook-URL field. Proves the descriptor <c>Fields</c> drive the form generically.
/// </summary>
[TestClass]
public class AlertProfileChannelFormTests
{
    // A /notifications/providers feed with one schema-driven channel (Slack, secret url) and
    // one schemaless channel (web push) — only the schema-driven one gets a generic section.
    private const string ProvidersJson = """
    [
      {"typeId":"webpush","displayName":"Web push","icon":"bell","description":"Browser push.","supportsTest":false,"category":"notification","fields":[]},
      {"typeId":"slack","displayName":"Slack","icon":"slack","description":"Posts to a Slack channel.","supportsTest":true,"category":"notification","fields":[
        {"key":"url","label":"Incoming webhook URL","kind":"secret","required":true,"help":"Slack incoming-webhook URL.","placeholder":"https://hooks.slack.com/services/…","options":[]}
      ]}
    ]
    """;

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/notifications/providers" => ProvidersJson,
                "/admin/alert-profiles" => "[]",
                _ => "{}",
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static BunitTestContext Ctx()
    {
        var ctx = new BunitTestContext();
        ctx.Services.AddMudServices();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var http = new HttpClient(new Handler()) { BaseAddress = new Uri("http://api.test") };
        ctx.Services.AddSingleton(new StatusApiClient(http));
        return ctx;
    }

    [TestMethod]
    public void AddProfile_RendersSlackChannel_EnablingRevealsSecretUrlField()
    {
        using var ctx = Ctx();
        var provider = ctx.RenderComponent<MudDialogProvider>();
        var cut = ctx.RenderComponent<AlertProfileListPanel>();

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "+ Add profile").Click();
        provider.WaitForAssertion(() => Assert.IsTrue(provider.Markup.Contains("ADD ALERT PROFILE")));

        // The schema-driven Slack channel gets a generic section; web push (no fields) does not.
        provider.WaitForAssertion(() => Assert.AreEqual(1, provider.FindAll(".ap-chan-slack").Count, "Slack channel toggle renders from its descriptor"));
        Assert.AreEqual(0, provider.FindAll(".ap-chan-webpush").Count, "schemaless web push has no generic section");

        // Its config field is hidden until the channel is enabled…
        Assert.AreEqual(0, provider.FindAll(".ap-cfg-slack-url").Count, "field hidden while the channel is off");

        // …enabling the channel reveals the (secret) webhook-URL field.
        provider.Find(".ap-chan-slack input").Change(true);
        provider.WaitForAssertion(() => Assert.AreEqual(1, provider.FindAll(".ap-cfg-slack-url").Count, "enabling the channel reveals its schema field"));
        Assert.AreEqual("password", provider.Find(".ap-cfg-slack-url input").GetAttribute("type"), "the secret field is a password input");
    }
}
