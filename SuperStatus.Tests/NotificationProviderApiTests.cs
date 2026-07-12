using SuperStatus.ApiService;
using SuperStatus.Data.ViewModels;
using SuperStatus.Services.Notifications;

namespace SuperStatus.Tests;

/// <summary>
/// #343 Phase 2: the anonymous GET /notifications/providers projection —
/// <see cref="NotificationProviderApi.ToViewModel"/> maps a channel descriptor to the
/// wire VM with the right category + test capability, so the descriptor contract the
/// Plugins page depends on cannot drift. Mirrors how the Blazor page tests cover the
/// consuming side; this pins the server projection independently.
/// </summary>
[TestClass]
public class NotificationProviderApiTests
{
    [TestMethod]
    public void ToViewModel_ProjectsDisplayMetadata_Category_AndTestCapability()
    {
        var descriptor = new NotificationDescriptor(
            typeId: "email",
            displayName: "Email (SMTP)",
            icon: "mail",
            description: "Sends alert emails through the operator-configured SMTP relay.",
            supportsTest: true);

        var vm = NotificationProviderApi.ToViewModel(descriptor);

        Assert.AreEqual("email", vm.TypeId);
        Assert.AreEqual("Email (SMTP)", vm.DisplayName);
        Assert.AreEqual("mail", vm.Icon);
        Assert.AreEqual("Sends alert emails through the operator-configured SMTP relay.", vm.Description);
        Assert.IsTrue(vm.SupportsTest);
        Assert.AreEqual(PluginCategories.Notification, vm.Category, "channels are always the notification category");
    }

    [TestMethod]
    public void ToViewModel_CarriesSupportsTestFalse_ForChannelsWithoutATestPath()
    {
        var descriptor = new NotificationDescriptor(
            typeId: "webpush",
            displayName: "Browser push",
            icon: "bell",
            description: "Pushes alerts to subscribed browsers via Web Push (VAPID).",
            supportsTest: false);

        var vm = NotificationProviderApi.ToViewModel(descriptor);

        Assert.AreEqual("webpush", vm.TypeId);
        Assert.IsFalse(vm.SupportsTest, "web push has no test-send path yet");
        Assert.AreEqual(PluginCategories.Notification, vm.Category);
    }

    [TestMethod]
    public void CheckAndNotificationCategories_AreDistinctStableStrings()
    {
        // The two catalogue sections group on these; they must stay distinct + stable.
        Assert.AreEqual("check", PluginCategories.Check);
        Assert.AreEqual("notification", PluginCategories.Notification);
        Assert.AreNotEqual(PluginCategories.Check, PluginCategories.Notification);
    }
}
