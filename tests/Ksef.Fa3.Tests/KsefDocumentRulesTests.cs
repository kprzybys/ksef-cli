using System.Text;

namespace Ksef.Fa3.Tests;

public class KsefDocumentRulesTests
{
    private static string ValidXml => TestInvoices.Fill("invoice-template-fa-3.xml");

    [Fact]
    public void ValidInvoice_PassesFullValidation()
    {
        Assert.Empty(Fa3Validator.Validate(Utf8(ValidXml)));
    }

    [Fact]
    public void Bom_IsRejected()
    {
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. Utf8(ValidXml)];

        Assert.Contains(KsefDocumentRules.Check(withBom), e => e.Message.Contains("BOM"));
    }

    [Fact]
    public void DeclaredNonUtf8Encoding_IsRejected()
    {
        string xml = "<?xml version=\"1.0\" encoding=\"windows-1250\"?>" + StripDeclaration(ValidXml);

        Assert.Contains(KsefDocumentRules.Check(Utf8(xml)), e => e.Message.Contains("windows-1250"));
    }

    [Fact]
    public void ProcessingInstruction_IsRejected()
    {
        string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><?xml-stylesheet href=\"styl.xsl\"?>" + StripDeclaration(ValidXml);

        Assert.Contains(KsefDocumentRules.Check(Utf8(xml)), e => e.Message.Contains("xml-stylesheet"));
    }

    [Theory]
    [InlineData("\u0080")]
    [InlineData("\uFDD0")]
    [InlineData("\U0001FFFE")]
    public void DiscouragedUnicodeCharacter_IsRejectedWithPosition(string character)
    {
        string valid = ValidXml;
        int at = valid.IndexOf("Warszawa", StringComparison.Ordinal) + 5;
        string xml = valid.Insert(at, character);

        Fa3ValidationError error = Assert.Single(KsefDocumentRules.Check(Utf8(xml)));
        Assert.Equal("KSeF", error.Source);
        Assert.True(error.Line > 1);
    }

    [Fact]
    public void MfTemplateWithDisallowedCharacters_IsRejected()
    {
        // XSD tego nie łapie — dlatego istnieją reguły KSeF.
        string xml = TestInvoices.Fill("invoice-template-fa-3-with-disallowed-unicode-characters.xml");

        Assert.Empty(Fa3Schema.Validate(new MemoryStream(Utf8(xml))));
        Assert.Contains(KsefDocumentRules.Check(Utf8(xml)), e => e.Message.Contains("Niedozwolony znak"));
    }

    [Fact]
    public void InvalidUtf8_IsRejected()
    {
        byte[] xml = [.. Utf8("<Faktura>"), 0xC3, 0x28, .. Utf8("</Faktura>")];

        Assert.Contains(KsefDocumentRules.Check(xml), e => e.Message.Contains("UTF-8"));
    }

    [Fact]
    public void OversizedInvoiceWithoutAttachment_IsRejected()
    {
        string padding = new(' ', KsefDocumentRules.MaxSizeWithoutAttachment);
        string xml = ValidXml.Replace("</Faktura>", padding + "</Faktura>");

        Assert.Contains(KsefDocumentRules.Check(Utf8(xml)), e => e.Message.Contains("bez załącznika"));
    }

    [Fact]
    public void InvoiceWithAttachment_GetsHigherLimit()
    {
        string xml = TestInvoices.Fill("invoice-template-fa-3-with-attachment.xml");
        string padded = xml.Replace("</Faktura>", new string(' ', KsefDocumentRules.MaxSizeWithoutAttachment) + "</Faktura>");

        Assert.Empty(KsefDocumentRules.Check(Utf8(padded)));
    }

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

    private static string StripDeclaration(string xml) =>
        xml.StartsWith("<?xml", StringComparison.Ordinal) ? xml[(xml.IndexOf("?>", StringComparison.Ordinal) + 2)..] : xml;
}
