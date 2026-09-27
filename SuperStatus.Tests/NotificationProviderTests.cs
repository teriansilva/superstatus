using SuperStatus.Data.Constants;
using SuperStatus.Data.Entities;
using SuperStatus.Services.Alerts;
using SuperStatus.Services.Notifications;

namespace SuperStatus.Tests;

/// <summary>
/// #343 Phase 1: the notification-provider seam — the registry's fail-fast + lookup
/// contract (mirroring CheckProviderRegistry) and the channel providers' result
/// mapping onto the unified <see cref="NotificationSendResult"/>.
/// </summary>
[TestClass]
public class NotificationProviderTests
{
    [TestMethod]
    public void Registry_duplicateTypeId_throws()
    {
        var providers = new INotificationProvider[] { new FakeProvider("email"), new FakeProvider("email") };
        Assert.ThrowsException<InvalidOperationException>(() => new NotificationProviderRegistry(providers));
    }

    [TestMethod]
    public void Registry_findsByTypeId_caseInsensitive_ordersDescriptors_andMissesGracefully()
    {
        var registry = new NotificationProviderRegistry(new INotificationProvider[]
        {
            new FakeProvider("webpush", "Browser push"),
            new FakeProvider("email", "Email (SMTP)"),
        });

        Assert.IsNotNull(registry.Find("EMAIL"), "lookup is case-insensitive");
        Assert.IsNull(registry.Find("nope"), "unknown id → null");
        Assert.IsNull(registry.Find(null), "null id → null");
        CollectionAssert.AreEqual(
            new[] { "Browser push", "Email (SMTP)" },
            registry.Descriptors.Select(d => d.DisplayName).ToList(),
            "descriptors are ordered by display name");
    }

    [TestMethod]
    public void EmailProvider_descriptor_isEmail_andSupportsTest()
    {
        var provider = new EmailNotificationProvider(new StubEmail(EmailSendResult.Sent("x")));
        Assert.AreEqual("email", provider.Descriptor.TypeId);
        Assert.IsTrue(provider.Descriptor.SupportsTest, "email has a real test send");
    }

    [TestMethod]
    public void WebPushProvider_descriptor_isWebPush_andDoesNotSupportTest()
    {
        var provider = new WebPushNotificationProvider(new StubPush(WebPushSendResult.Skipped("none")));
        Assert.AreEqual("webpush", provider.Descriptor.TypeId);
        Assert.IsFalse(provider.Descriptor.SupportsTest, "web push has no test-send path yet");
    }

    [TestMethod]
    public async Task EmailProvider_mapsEveryStatus_ontoNotificationResult()
    {
        var ctx = new NotificationContext(NewCheck(), AlertTrigger.Failure, "ops@example.com");

        var sent = await new EmailNotificationProvider(new StubEmail(EmailSendResult.Sent("ops@example.com"))).SendAsync(ctx);
        Assert.AreEqual(NotificationOutcome.Sent, sent.Outcome);
        Assert.AreEqual("ops@example.com", sent.Target);

        var skipped = await new EmailNotificationProvider(new StubEmail(EmailSendResult.Skipped("no recipients"))).SendAsync(ctx);
        Assert.AreEqual(NotificationOutcome.Skipped, skipped.Outcome);
        Assert.AreEqual("no recipients", skipped.Detail);

        var failed = await new EmailNotificationProvider(new StubEmail(EmailSendResult.Failed("ops@example.com", "smtp refused"))).SendAsync(ctx);
        Assert.AreEqual(NotificationOutcome.Failed, failed.Outcome);
        Assert.AreEqual("smtp refused", failed.Detail);
    }

    [TestMethod]
    public async Task WebPushProvider_mapsEveryStatus_ontoNotificationResult()
    {
        var ctx = new NotificationContext(NewCheck(), AlertTrigger.Failure);

        var sent = await new WebPushNotificationProvider(new StubPush(WebPushSendResult.Sent("2 device(s)"))).SendAsync(ctx);
        Assert.AreEqual(NotificationOutcome.Sent, sent.Outcome);
        Assert.AreEqual("2 device(s)", sent.Target);

        var skipped = await new WebPushNotificationProvider(new StubPush(WebPushSendResult.Skipped("no subscribed devices"))).SendAsync(ctx);
        Assert.AreEqual(NotificationOutcome.Skipped, skipped.Outcome);
        Assert.AreEqual("no subscribed devices", skipped.Detail);

        var failed = await new WebPushNotificationProvider(new StubPush(WebPushSendResult.Failed("0 device(s)", "push service 500"))).SendAsync(ctx);
        Assert.AreEqual(NotificationOutcome.Failed, failed.Outcome);
        Assert.AreEqual("push service 500", failed.Detail);
    }

    private static StatusCheck NewCheck() => new()
    {
        Title = "svc",
        StatusCheckUrl = "https://svc.test",
        ServiceLogoUrl = string.Empty,
    };

    private sealed class FakeProvider(string typeId, string displayName = "x") : INotificationProvider
    {
        public NotificationDescriptor Descriptor { get; } = new(typeId, displayName, "x");
        public Task<NotificationSendResult> SendAsync(NotificationContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(NotificationSendResult.Skipped("noop"));
    }

    private sealed class StubEmail(EmailSendResult result) : IEmailNotifier
    {
        public bool IsConfigured(SiteSettings settings) => true;
        public Task<EmailSendResult> SendAlertAsync(StatusCheck check, AlertTrigger trigger, string? recipientsOverride = null, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
        public Task<EmailSendResult> SendTestAsync(string? toOverride, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }

    private sealed class StubPush(WebPushSendResult result) : IWebPushNotifier
    {
        public bool IsConfigured(SiteSettings settings) => true;
        public Task<WebPushSendResult> SendAlertAsync(StatusCheck check, AlertTrigger trigger, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }
}
