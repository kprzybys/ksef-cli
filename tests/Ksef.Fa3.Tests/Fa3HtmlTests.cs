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

    private static string Render(string xml)
    {
        using MemoryStream output = new();
        Fa3Html.Render(new MemoryStream(Encoding.UTF8.GetBytes(xml)), output);
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
