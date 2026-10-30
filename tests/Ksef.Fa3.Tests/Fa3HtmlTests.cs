using System.Text;

namespace Ksef.Fa3.Tests;

public class Fa3HtmlTests
{
    [Theory]
    [MemberData(nameof(Fa3SchemaTests.ValidTemplates), MemberType = typeof(Fa3SchemaTests))]
    public void Render_ProducesHtmlForMfTemplates(string template)
    {
        string html = Render(TestInvoices.Fill(template));

        Assert.Contains("e-FAKTURA KSeF", html);
        Assert.Contains("1111111111", html);
    }

    [Fact]
    public void Render_ResolvesDictionariesOffline()
    {
        // Nazwa kraju pochodzi ze słownika KodyKrajow czytanego przez document() z zasobów wbudowanych.
        string html = Render(TestInvoices.Fill("invoice-template-fa-3.xml"));

        Assert.Contains("<span class=\"nazwa-dla-kodu\">(POLSKA)</span>", html);
    }

    [Fact]
    public void Render_WritesUtf8WithoutBom()
    {
        // Wynik idzie na stdout — BOM psułby potoki.
        byte[] html = RenderBytes(TestInvoices.Fill("invoice-template-fa-3.xml"));

        Assert.False(html.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
    }

    private static string Render(string xml) => Encoding.UTF8.GetString(RenderBytes(xml));

    private static byte[] RenderBytes(string xml)
    {
        using MemoryStream output = new();
        Fa3Html.Render(new MemoryStream(Encoding.UTF8.GetBytes(xml)), output);
        return output.ToArray();
    }
}
