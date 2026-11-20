using Playnite.Core.Descriptions;
using Playnite.SDK.Descriptions;
using Xunit;

namespace Playnite.Core.Tests.Descriptions;

public sealed class HtmlDescriptionSanitizerTests
{
    private readonly HtmlDescriptionSanitizer sanitizer = new();

    [Fact]
    public void Allowed_markup_and_links_survive()
    {
        var result = sanitizer.Sanitize(
            "<p>Hello <b>world</b> <a href=\"https://example.com/page\">link</a></p>");

        Assert.Equal(
            "<p>Hello <b>world</b> <a href=\"https://example.com/page\" rel=\"noreferrer noopener\">link</a></p>",
            result.Html);
        Assert.Equal(0, result.RemovedElements);
        Assert.Equal(0, result.RemovedAttributes);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Script_element_and_its_content_are_removed()
    {
        var result = sanitizer.Sanitize(
            "<p>before</p><script>alert('x')</script><p>after</p>");

        Assert.Equal("<p>before</p><p>after</p>", result.Html);
        Assert.Equal(1, result.RemovedElements);
    }

    [Fact]
    public void Mixed_case_script_is_removed()
    {
        var result = sanitizer.Sanitize("<ScRiPt>alert(1)</sCrIpT>");
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void Event_handler_attributes_are_removed()
    {
        var result = sanitizer.Sanitize("<p onclick=\"steal()\" onmouseover='x'>text</p>");

        Assert.Equal("<p>text</p>", result.Html);
        Assert.Equal(2, result.RemovedAttributes);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/x")]
    [InlineData("/relative/path")]
    [InlineData("//evil.example/x")]
    [InlineData("https://user:pass@example.com/x")]
    [InlineData("")]
    public void Unsafe_link_targets_are_dropped_but_the_anchor_text_survives(string href)
    {
        var result = sanitizer.Sanitize($"<a href=\"{href}\">click</a>");

        Assert.Equal("<a>click</a>", result.Html);
        Assert.Equal(1, result.RemovedAttributes);
    }

    [Theory]
    [InlineData("jav&#x61;script:alert(1)")]
    [InlineData("jav&#97;script:alert(1)")]
    [InlineData("java&#10;script:alert(1)")]
    [InlineData("&#106;avascript:alert(1)")]
    public void Entity_encoded_javascript_targets_are_rejected(string href)
    {
        var result = sanitizer.Sanitize($"<a href=\"{href}\">click</a>");
        Assert.Equal("<a>click</a>", result.Html);
    }

    [Fact]
    public void Control_characters_in_a_link_are_rejected()
    {
        var result = sanitizer.Sanitize("<a href=\"java\tscript:alert(1)\">click</a>");
        Assert.Equal("<a>click</a>", result.Html);
    }

    [Fact]
    public void Iframe_is_removed_with_its_content()
    {
        var result = sanitizer.Sanitize(
            "<p>a</p><iframe src=\"https://evil.example\">fallback</iframe><p>b</p>");

        Assert.Equal("<p>a</p><p>b</p>", result.Html);
    }

    [Theory]
    [InlineData("object")]
    [InlineData("embed")]
    [InlineData("svg")]
    [InlineData("math")]
    [InlineData("template")]
    [InlineData("textarea")]
    [InlineData("noscript")]
    public void Active_or_foreign_content_is_removed_with_its_content(string element)
    {
        var result = sanitizer.Sanitize(
            $"<p>a</p><{element}>payload</{element}><p>b</p>");

        Assert.Equal("<p>a</p><p>b</p>", result.Html);
    }

    [Fact]
    public void Style_element_and_style_attribute_are_removed()
    {
        var result = sanitizer.Sanitize(
            "<style>body{background:url(https://evil.example)}</style>" +
            "<div style=\"background:url(https://evil.example)\">text</div>");

        Assert.Equal("<div>text</div>", result.Html);
    }

    [Fact]
    public void Images_and_other_remote_resource_tags_are_removed()
    {
        var result = sanitizer.Sanitize(
            "<p>a</p><img src=\"https://evil.example/pixel\" onerror=\"steal()\"><p>b</p>");

        Assert.Equal("<p>a</p><p>b</p>", result.Html);
        Assert.DoesNotContain("evil", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_tags_are_dropped_but_their_text_survives()
    {
        var result = sanitizer.Sanitize("<marquee>hello</marquee><custom-tag>x</custom-tag>");

        Assert.Equal("hellox", result.Html);
        Assert.Equal(2, result.RemovedElements);
    }

    [Fact]
    public void Attributes_other_than_a_validated_href_are_removed()
    {
        var result = sanitizer.Sanitize(
            "<a href=\"https://example.com/x\" target=\"_blank\" title=\"t\" class=\"c\">x</a>");

        Assert.Equal(
            "<a href=\"https://example.com/x\" rel=\"noreferrer noopener\">x</a>",
            result.Html);
        Assert.Equal(3, result.RemovedAttributes);
    }

    [Fact]
    public void Duplicate_href_uses_the_first_value()
    {
        var result = sanitizer.Sanitize(
            "<a href=\"https://good.example/x\" href=\"javascript:alert(1)\">x</a>");

        Assert.Equal(
            "<a href=\"https://good.example/x\" rel=\"noreferrer noopener\">x</a>",
            result.Html);
    }

    [Fact]
    public void Surrounding_whitespace_in_href_is_trimmed()
    {
        var result = sanitizer.Sanitize("<a href=\"  https://example.com/x  \">x</a>");

        Assert.Equal(
            "<a href=\"https://example.com/x\" rel=\"noreferrer noopener\">x</a>",
            result.Html);
    }

    [Fact]
    public void Html_entities_in_text_are_preserved_as_text_not_markup()
    {
        var result = sanitizer.Sanitize("<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>");

        Assert.Equal("<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>", result.Html);
        Assert.DoesNotContain("<script>", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Numeric_entities_in_text_are_normalized()
    {
        var result = sanitizer.Sanitize("<p>&#x3C;b&#x3E;not bold&#x3C;/b&#x3E;</p>");
        Assert.Equal("<p>&lt;b&gt;not bold&lt;/b&gt;</p>", result.Html);
    }

    [Fact]
    public void Named_entities_survive()
    {
        var result = sanitizer.Sanitize("<p>Tom &amp; Jerry&nbsp;&mdash; a classic</p>");
        Assert.Equal("<p>Tom &amp; Jerry\u00a0\u2014 a classic</p>", result.Html);
    }

    [Fact]
    public void Comments_and_doctypes_are_removed()
    {
        var result = sanitizer.Sanitize("a<!-- hidden -->b<!DOCTYPE html>c<?xml version=\"1.0\"?>d");
        Assert.Equal("abcd", result.Html);
    }

    [Fact]
    public void Malformed_angle_brackets_are_escaped()
    {
        Assert.Equal("a &lt; b", sanitizer.Sanitize("a < b").Html);
        Assert.Equal("&lt;3", sanitizer.Sanitize("<3").Html);
        Assert.Equal("1 &lt;= 2", sanitizer.Sanitize("1 <= 2").Html);
    }

    [Fact]
    public void Out_of_order_closing_tags_are_rebalanced()
    {
        var result = sanitizer.Sanitize("<b><i>x</b>y");
        Assert.Equal("<b><i>x</i></b>y", result.Html);
    }

    [Fact]
    public void Unclosed_tags_are_closed_at_the_end()
    {
        var result = sanitizer.Sanitize("<p>hello");
        Assert.Equal("<p>hello</p>", result.Html);
    }

    [Fact]
    public void Void_elements_do_not_remain_open()
    {
        Assert.Equal("<p>a<br>b</p>", sanitizer.Sanitize("<p>a<br>b</p>").Html);
        Assert.Equal("<hr>", sanitizer.Sanitize("<hr/>").Html);
    }

    [Fact]
    public void Null_and_empty_input_produce_an_empty_result()
    {
        Assert.True(sanitizer.Sanitize(null).IsEmpty);
        Assert.True(sanitizer.Sanitize(string.Empty).IsEmpty);
    }

    [Fact]
    public void Control_characters_are_removed_from_text()
    {
        Assert.Equal("abc", sanitizer.Sanitize("a\u0001b\u007fc").Html);
    }

    [Fact]
    public void Depth_is_bounded()
    {
        var input = string.Concat(Enumerable.Repeat("<div>", 80)) + "text" +
            string.Concat(Enumerable.Repeat("</div>", 80));

        var result = sanitizer.Sanitize(input);

        Assert.True(result.RemovedElements >= 16);
        Assert.Equal(HtmlDescriptionSanitizer.MaxDepth, CountOccurrences(result.Html, "<div>"));
        Assert.Equal(HtmlDescriptionSanitizer.MaxDepth, CountOccurrences(result.Html, "</div>"));
    }

    [Fact]
    public void Overlong_input_is_truncated()
    {
        var input = new string('a', HtmlDescriptionSanitizer.MaxInputLength + 10);
        var result = sanitizer.Sanitize(input);

        Assert.True(result.Truncated);
        Assert.Equal(HtmlDescriptionSanitizer.MaxInputLength, result.Html.Length);
    }

    [Fact]
    public void Unterminated_start_tag_is_dropped()
    {
        var result = sanitizer.Sanitize("<a href=\"https://example.com/x");
        Assert.True(result.IsEmpty);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Sanitizer_never_emits_active_markup_for_a_hostile_mixture()
    {
        const string hostile =
            "<div onclick=\"x\"><script>alert(1)</script><img src=x onerror=y>" +
            "<a href=\"javascript:alert(1)\">a</a><iframe src=\"https://evil\"></iframe>" +
            "<style>@import 'https://evil/x.css';</style><p style=\"x\">safe</p></div>";

        var result = sanitizer.Sanitize(hostile);

        Assert.Equal("<div><a>a</a><p>safe</p></div>", result.Html);
        Assert.DoesNotContain("<script", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iframe", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style", result.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://evil", result.Html, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
