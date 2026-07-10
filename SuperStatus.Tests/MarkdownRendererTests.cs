using SuperStatus.Web;

namespace SuperStatus.Tests;

/// <summary>
/// #349 — incident markdown rendering. Guards the stored-XSS boundary
/// (<see cref="MarkdownRenderer.ToSafeHtml"/> feeds <c>(MarkupString)</c> on the
/// public incident log): happy-path formatting, injection stripped, and the
/// invariants (null/blank → empty, legacy plain text → a paragraph).
/// </summary>
[TestClass]
public class MarkdownRendererTests
{
    // ---- happy path: markdown becomes formatted HTML -----------------------
    [TestMethod]
    public void Bold_And_Emphasis_Render()
    {
        var html = MarkdownRenderer.ToSafeHtml("We're at **p95** over _budget_.");
        StringAssert.Contains(html, "<strong>p95</strong>");
        StringAssert.Contains(html, "<em>budget</em>");
    }

    [TestMethod]
    public void Headings_Lists_Code_Render()
    {
        var html = MarkdownRenderer.ToSafeHtml("### Update\n\n- rolled back\n- `deploy` reverted");
        StringAssert.Contains(html, "<h3");
        StringAssert.Contains(html, "<ul>");
        StringAssert.Contains(html, "<li>");
        StringAssert.Contains(html, "<code>deploy</code>");
    }

    [TestMethod]
    public void SafeLink_IsKept_AndHardened()
    {
        var html = MarkdownRenderer.ToSafeHtml("See [status](https://status.example.com).");
        StringAssert.Contains(html, "href=\"https://status.example.com\"");
        StringAssert.Contains(html, "nofollow");   // external links hardened with rel
        StringAssert.Contains(html, "noopener");
    }

    // ---- injection is stripped (stored-XSS boundary) -----------------------
    [TestMethod]
    public void ScriptTag_IsStripped()
    {
        var html = MarkdownRenderer.ToSafeHtml("hi <script>alert(1)</script> there");
        Assert.IsFalse(html.Contains("<script", StringComparison.OrdinalIgnoreCase), html);
        Assert.IsFalse(html.Contains("alert(1)") && html.Contains("<script"), html);
    }

    [TestMethod]
    public void ImgOnerror_IsNotEmittedAsLiveElement()
    {
        // Raw HTML in the source is escaped to inert text (DisableHtml), never a
        // live element — so no <img> and no active onerror handler reach the DOM.
        var html = MarkdownRenderer.ToSafeHtml("<img src=x onerror=alert(1)>");
        Assert.IsFalse(html.Contains("<img", StringComparison.OrdinalIgnoreCase), html);
        StringAssert.Contains(html, "&lt;img");   // proven neutralised (escaped), not passed through
    }

    [TestMethod]
    public void JavascriptScheme_Link_IsNeutralised()
    {
        var html = MarkdownRenderer.ToSafeHtml("[click](javascript:alert(1))");
        Assert.IsFalse(html.Contains("javascript:", StringComparison.OrdinalIgnoreCase), html);
    }

    [TestMethod]
    public void RawHtmlAnchor_WithHandler_IsNotEmittedAsLiveMarkup()
    {
        // A hand-written anchor with an inline handler is escaped, not emitted as a
        // live <a> — so no clickable element carries the onclick.
        var html = MarkdownRenderer.ToSafeHtml("<a href=\"#\" onclick=\"steal()\">x</a>");
        Assert.IsFalse(html.Contains("<a", StringComparison.OrdinalIgnoreCase), html);
        StringAssert.Contains(html, "&lt;a");
    }

    // ---- invariants --------------------------------------------------------
    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("\n\t ")]
    public void NullOrBlank_YieldsEmpty(string? input)
    {
        Assert.AreEqual(string.Empty, MarkdownRenderer.ToSafeHtml(input));
    }

    [TestMethod]
    public void LegacyPlainText_RendersAsParagraph()
    {
        // Pre-#349 descriptions are plain text; they must still render, unchanged
        // in meaning, as a simple paragraph.
        var html = MarkdownRenderer.ToSafeHtml("Service degraded since 15:20 UTC.");
        StringAssert.Contains(html, "<p>Service degraded since 15:20 UTC.</p>");
    }
}
