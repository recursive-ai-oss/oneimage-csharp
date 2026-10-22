using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.AspNetCore.WebUtilities;

namespace OneImage.Web;

/// <summary>
/// Builds the URL of a processed image in Razor: <c>&lt;img src="~/photos/a.jpg" oi-width="300" oi-format="webp" /&gt;</c> becomes
/// <c>src="/photos/a.jpg?width=300&amp;format=webp&amp;hmac=..."</c>. Every <c>oi-</c> attribute is a command (<c>oi-width</c>, <c>oi-height</c>, <c>oi-fit</c>, <c>oi-preset</c> ...);
/// the token is added when a signing key is configured. Register it with <c>@addTagHelper *, OneImage.Web</c>.
/// </summary>
[HtmlTargetElement("img", Attributes = "src,oi-*")]
public sealed class ImageTagHelper : TagHelper
{
    private const string Prefix = "oi-";
    private readonly ImageUrlSigner _signer;
    private readonly IUrlHelperFactory _urlHelperFactory;

    /// <summary>Initializes the tag helper.</summary>
    /// <param name="signer">Adds the token.</param>
    /// <param name="urlHelperFactory">Resolves <c>~/</c> paths.</param>
    public ImageTagHelper(ImageUrlSigner signer, IUrlHelperFactory urlHelperFactory)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(urlHelperFactory);
        _signer = signer;
        _urlHelperFactory = urlHelperFactory;
    }

    /// <summary>Gets or sets the commands, keyed by the part of the attribute name after <c>oi-</c>.</summary>
    [HtmlAttributeName(DictionaryAttributePrefix = Prefix)]
    public IDictionary<string, string> Commands { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the view context, used to resolve <c>~/</c> paths.</summary>
    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext? ViewContext { get; set; }

    /// <inheritdoc />
    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);
        string? src = context.AllAttributes["src"]?.Value?.ToString();
        if (string.IsNullOrEmpty(src) || Commands.Count == 0)
        {
            return;
        }

        if (src.StartsWith("~/", StringComparison.Ordinal) && ViewContext is not null)
        {
            src = _urlHelperFactory.GetUrlHelper(ViewContext).Content(src);
        }

        string url = src;
        foreach (KeyValuePair<string, string> command in Commands)
        {
            url = QueryHelpers.AddQueryString(url, command.Key.ToLowerInvariant(), command.Value);
        }

        output.Attributes.SetAttribute("src", _signer.Sign(url));
    }
}
