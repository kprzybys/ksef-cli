using System.Xml;
using System.Xml.Xsl;

namespace Ksef.Fa3;

/// <summary>Wizualizacja faktury FA(3) do HTML oficjalnym arkuszem MF (crd.gov.pl, styl.xsl), offline.</summary>
public static class Fa3Html
{
    private const string ResourcePrefix = "Ksef.Fa3.Stylesheets.";

    private static readonly EmbeddedResourceResolver Resolver = new(typeof(Fa3Html).Assembly, ResourcePrefix);

    private static readonly Lazy<XslCompiledTransform> Transform = new(LoadTransform);

    public static void Render(Stream xml, Stream html)
    {
        XmlReaderSettings readerSettings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using XmlReader reader = XmlReader.Create(xml, readerSettings);
        using XmlWriter writer = XmlWriter.Create(html, Transform.Value.OutputSettings);
        // Resolver obsługuje document() w arkuszu (słowniki krajów, walut, urzędów) z zasobów wbudowanych.
        Transform.Value.Transform(reader, null, writer, Resolver);
    }

    private static XslCompiledTransform LoadTransform()
    {
        XslCompiledTransform transform = new();
        using XmlReader stylesheet = XmlReader.Create(
            Resolver.GetEntity(Resolver.ResolveUri(null, "styl.xsl"), null, null) as Stream
                ?? throw new InvalidOperationException("Brak wbudowanego arkusza styl.xsl."),
            new XmlReaderSettings { XmlResolver = null },
            "embedded:///styl.xsl");
        transform.Load(stylesheet, new XsltSettings(enableDocumentFunction: true, enableScript: false), Resolver);
        return transform;
    }
}
