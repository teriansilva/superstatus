using AngleSharp.Dom;
using Ganss.Xss;
using Markdig;

namespace SuperStatus.Web;

/// <summary>
/// Renders operator-authored incident markdown (#349) to safe HTML for display.
///
/// The incident <c>Description</c> is stored raw (markdown) and rendered here at
/// display time. Because it reaches the public <c>/incidents</c> surface, this is
/// a stored-XSS boundary: the Markdig pipeline has raw HTML disabled (so any
/// embedded HTML is escaped, never emitted) and the generated HTML is then run
/// through a tight <see cref="HtmlSanitizer"/> allowlist. Plain-text descriptions
/// (the pre-#349 format) are valid markdown and render as a paragraph unchanged.
/// </summary>
public static class MarkdownRenderer
{
    // Immutable + thread-safe once built.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()   // raw inline/block HTML in the source is escaped, not passed through
        .UseAutoLinks()  // bare URLs become links
        .Build();

    private static readonly HtmlSanitizer Sanitizer = CreateSanitizer();

    /// <summary>Markdown → sanitized HTML. Null/blank input yields an empty string.</summary>
    public static string ToSafeHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        var html = Markdown.ToHtml(markdown, Pipeline);
        return Sanitizer.Sanitize(html);
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var s = new HtmlSanitizer();

        // Tight allowlist — prose formatting only, no embeds / media / tables / styles.
        s.AllowedTags.Clear();
        foreach (var tag in new[]
                 {
                     "p", "br", "hr", "strong", "em", "b", "i", "del", "code", "pre",
                     "blockquote", "ul", "ol", "li", "h1", "h2", "h3", "h4", "h5", "h6", "a",
                 })
        {
            s.AllowedTags.Add(tag);
        }

        s.AllowedAttributes.Clear();
        s.AllowedAttributes.Add("href");
        s.AllowedAttributes.Add("rel");

        s.AllowedSchemes.Clear();
        s.AllowedSchemes.Add("http");
        s.AllowedSchemes.Add("https");
        s.AllowedSchemes.Add("mailto");

        s.AllowDataAttributes = false;
        s.AllowedCssProperties.Clear();   // no inline styles at all

        // Harden every surviving link (external, untrusted).
        s.PostProcessNode += (_, e) =>
        {
            if (e.Node is IElement el && el.NodeName.Equals("A", StringComparison.OrdinalIgnoreCase))
            {
                el.SetAttribute("rel", "nofollow noopener noreferrer");
            }
        };

        return s;
    }
}
