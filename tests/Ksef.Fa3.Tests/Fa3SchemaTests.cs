using System.Text;

using System.Text.RegularExpressions;

namespace Ksef.Fa3.Tests;

public class Fa3SchemaTests
{
    // Wzory MF FA(3), które po wypełnieniu znaczników powinny być zgodne ze schematem.
    // invoice-template-fa-3-with-multiple-Subject3.xml pominięty: ma IDWew przed Nazwa w Podmiot3, czego schemat nie dopuszcza.
    public static TheoryData<string> ValidTemplates =>
    [
        "invoice-template-fa-3.xml",
        "invoice-template-fa-3-with-attachment.xml",
        "invoice-template-fa-3-with-custom-Subject2.xml",
        "invoice-template-fa-3-with-custom-Subject3.xml",
    ];

    [Theory]
    [MemberData(nameof(ValidTemplates))]
    public void FilledTemplate_IsValid(string template)
    {
        Assert.Empty(Validate(FillTemplate(template)));
    }

    [Theory]
    [MemberData(nameof(ValidTemplates))]
    public void FilledTemplate_SurvivesRoundTripThroughGeneratedClasses(string template)
    {
        Faktura invoice = Fa3Serializer.Load(ToStream(FillTemplate(template)));

        using MemoryStream output = new();
        Fa3Serializer.Save(invoice, output);
        output.Position = 0;

        Assert.Empty(Fa3Schema.Validate(output));
    }

    [Fact]
    public void Load_ReadsKeyFields()
    {
        Faktura invoice = Fa3Serializer.Load(ToStream(FillTemplate("invoice-template-fa-3.xml")));

        Assert.Equal("1111111111", invoice.Podmiot1.DaneIdentyfikacyjne.Nip);
        Assert.Contains("FV/TEST/1", invoice.Fa.P2); // wzór MF osadza numer w dłuższym ciągu
    }

    [Fact]
    public void UnfilledTemplate_ReportsPlaceholderNip()
    {
        var errors = Validate(ReadTemplate("invoice-template-fa-3.xml"));

        Assert.Contains(errors, e => e.Message.Contains("#nip#") && e.Line > 0);
    }

    [Fact]
    public void Fa2Document_IsRejected()
    {
        var errors = Validate(ReadTemplate("invoice-template-fa-2.xml"));

        Assert.Contains(errors, e => e.Message.Contains("oczekiwano Faktura"));
    }

    [Fact]
    public void MalformedXml_IsReportedNotThrown()
    {
        var errors = Validate("<Faktura xmlns=\"http://crd.gov.pl/wzor/2025/06/25/13775/\"><Naglowek>");

        Assert.NotEmpty(errors);
    }

    private static IReadOnlyList<Fa3ValidationError> Validate(string xml) => Fa3Schema.Validate(ToStream(xml));

    private static string ReadTemplate(string name) => TestInvoices.Read(name);

    private static string FillTemplate(string name) => TestInvoices.Fill(name);

    private static MemoryStream ToStream(string xml) => new(Encoding.UTF8.GetBytes(xml));
}
