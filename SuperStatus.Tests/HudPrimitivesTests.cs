using Bunit;
using Microsoft.AspNetCore.Components;
using SuperStatus.Web.Components.Hud;
using BunitTestContext = Bunit.TestContext;

namespace SuperStatus.Tests;

[TestClass]
public class HudPrimitivesTests
{
    // Each test creates its own bUnit TestContext (BunitTestContext aliased
    // to avoid a name clash with MSTest's TestContext) so per-method state
    // doesn't leak across cases.

    [TestMethod]
    public void HudPanel_RendersFourCornerBracketsAndChildContent()
    {
        using var ctx = new BunitTestContext();
        var cut = ctx.RenderComponent<HudPanel>(parameters => parameters
            .AddChildContent("inner-payload"));

        Assert.AreEqual(4, cut.FindAll(".cnr").Count, "HudPanel must render four corner brackets.");
        cut.Find(".panel.frame"); // throws if missing
        Assert.IsTrue(cut.Markup.Contains("inner-payload"));
    }

    [TestMethod]
    public void HudPanel_PrimaryAccent_AddsPrimaryClass()
    {
        using var ctx = new BunitTestContext();
        var cut = ctx.RenderComponent<HudPanel>(p => p
            .Add(x => x.Accent, "primary")
            .AddChildContent("x"));

        cut.Find(".panel.primary");
    }

    [TestMethod]
    public void HudPanel_CriticalAccent_AddsCriticalClass()
    {
        using var ctx = new BunitTestContext();
        var cut = ctx.RenderComponent<HudPanel>(p => p
            .Add(x => x.Accent, "critical")
            .AddChildContent("x"));

        cut.Find(".panel.critical");
    }

    [TestMethod]
    public void HudPanel_CallerClassAttribute_MergesWithBuiltInClasses()
    {
        // Regression for Hermes #99 review: Razor splat overrode the
        // explicit `class=...` literal, so `<HudPanel class="external">`
        // rendered as just `class="external"` and dropped frame / panel /
        // accent + the corner-bracket styling that depends on them.
        using var ctx = new BunitTestContext();
        var cut = ctx.RenderComponent<HudPanel>(p => p
            .Add(x => x.Accent, "critical")
            .AddUnmatched("class", "external one-more")
            .AddChildContent("x"));

        // All required classes must survive.
        var div = cut.Find("div");
        var classes = (div.GetAttribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        CollectionAssert.Contains(classes, "frame");
        CollectionAssert.Contains(classes, "panel");
        CollectionAssert.Contains(classes, "critical");
        CollectionAssert.Contains(classes, "external");
        CollectionAssert.Contains(classes, "one-more");

        // And the four corner brackets must still render.
        Assert.AreEqual(4, cut.FindAll(".cnr").Count);
    }

    [TestMethod]
    public void HudPanel_NonClassSplatAttributes_ForwardedToOuterDiv()
    {
        using var ctx = new BunitTestContext();
        var cut = ctx.RenderComponent<HudPanel>(p => p
            .AddUnmatched("data-test", "marker")
            .AddUnmatched("style", "display: grid;")
            .AddChildContent("x"));

        var div = cut.Find("div");
        Assert.AreEqual("marker", div.GetAttribute("data-test"));
        Assert.AreEqual("display: grid;", div.GetAttribute("style"));
    }

    [TestMethod]
    public void HudLed_UnknownStatusValue_DropsToNeutralClass()
    {
        using var ctx = new BunitTestContext();
        var cut = ctx.RenderComponent<HudLed>(p => p
            .Add(x => x.Status, "nonsense"));

        var led = cut.Find(".led");
        // Only the bare .led class — no status modifier applied for invalid input.
        Assert.AreEqual("led", led.GetAttribute("class")?.Trim());
    }

    [TestMethod]
    [DataRow("up")]
    [DataRow("degraded")]
    [DataRow("down")]
    [DataRow("unknown")]
    public void HudLed_KnownStatusValues_EmitMatchingClass(string status)
    {
        using var ctx = new BunitTestContext();
        var cut = ctx.RenderComponent<HudLed>(p => p
            .Add(x => x.Status, status));

        cut.Find($".led.{status}");
    }

    [TestMethod]
    public void HudTag_WithStatus_RendersNestedLed()
    {
        using var ctx = new BunitTestContext();
        var cut = ctx.RenderComponent<HudTag>(p => p
            .Add(x => x.Status, "up")
            .Add(x => x.Tone, "accent")
            .AddChildContent("STATUS//ONLINE"));

        cut.Find(".tag.accent .led.up");
        Assert.IsTrue(cut.Markup.Contains("STATUS//ONLINE"));
    }

    [TestMethod]
    public void HudCallsign_EmitsLabelSepValueMeta()
    {
        using var ctx = new BunitTestContext();
        var cut = ctx.RenderComponent<HudCallsign>(p => p
            .Add(x => x.Label, "SERVICE")
            .Add(x => x.Value, "Acme Service")
            .Add(x => x.Meta, "POLL 10s"));

        cut.Find(".callsign .label");
        cut.Find(".callsign .sep");
        cut.Find(".callsign .value");
        cut.Find(".callsign .meta");
        Assert.IsTrue(cut.Markup.Contains("SERVICE"));
        Assert.IsTrue(cut.Markup.Contains("Acme Service"));
        Assert.IsTrue(cut.Markup.Contains("POLL 10s"));
    }

    [TestMethod]
    public void HudCallsign_NoLabelOrMeta_SkipsThoseCells()
    {
        using var ctx = new BunitTestContext();
        var cut = ctx.RenderComponent<HudCallsign>(p => p
            .Add(x => x.Value, "Just a value"));

        Assert.AreEqual(0, cut.FindAll(".callsign .label").Count);
        Assert.AreEqual(0, cut.FindAll(".callsign .sep").Count);
        Assert.AreEqual(0, cut.FindAll(".callsign .meta").Count);
        cut.Find(".callsign .value");
    }

    [TestMethod]
    public void HudTelemetryStrip_WrapsChildChips()
    {
        using var ctx = new BunitTestContext();
        var cut = ctx.RenderComponent<HudTelemetryStrip>(p => p
            .AddChildContent<HudChip>(cp => cp.Add(c => c.K, "Up").Add(c => c.V, "14").Add(c => c.Tone, "accent"))
            .AddChildContent<HudChip>(cp => cp.Add(c => c.K, "Down").Add(c => c.V, "0").Add(c => c.Tone, "critical")));

        cut.Find(".telemetry-strip");
        Assert.AreEqual(2, cut.FindAll(".telemetry-strip .chip").Count);
        cut.Find(".telemetry-strip .chip .v.accent");
        cut.Find(".telemetry-strip .chip .v.critical");
    }

    [TestMethod]
    public void HudKeyValue_WithLedAndTone_EmitsBothModifiers()
    {
        using var ctx = new BunitTestContext();
        var cut = ctx.RenderComponent<HudKeyValueGrid>(p => p.AddChildContent<HudKeyValue>(cp => cp
            .Add(c => c.K, "alpha")
            .Add(c => c.V, "98.94%")
            .Add(c => c.Led, "down")
            .Add(c => c.Tone, "down")));

        cut.Find(".kv .k .led.down");
        cut.Find(".kv .v.down");
    }
}
