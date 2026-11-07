using System.Text;

namespace Ksef.Fa3.Tests;

public class Fa3VatArithmeticTests
{
    private static string ValidXml => TestInvoices.Fill("invoice-template-fa-3.xml");

    [Fact]
    public void CorrectInvoice_HasNoVatErrors()
    {
        Assert.Empty(Fa3VatArithmetic.Check(Utf8(ValidXml)));
        Assert.Empty(Fa3Validator.Validate(Utf8(ValidXml)));
    }

    [Fact]
    public void WrongP14_IsReportedWithPosition()
    {
        string xml = ValidXml.Replace("<P_14_1>10.03</P_14_1>", "<P_14_1>11.03</P_14_1>", StringComparison.Ordinal);

        Fa3ValidationError error = Assert.Single(Fa3Validator.Validate(Utf8(xml)));

        Assert.Contains("P_14_1 = 11.03", error.Message);
        Assert.True(error.Line > 1);
    }

    [Fact]
    public void P14_OffByOneGrosz_IsTolerated()
    {
        string xml = ValidXml.Replace("<P_14_1>10.03</P_14_1>", "<P_14_1>10.04</P_14_1>", StringComparison.Ordinal);

        Assert.Empty(Fa3VatArithmetic.Check(Utf8(xml)));
    }

    [Fact]
    public void WrongP13_IsReported()
    {
        string xml = ValidXml.Replace("<P_13_1>43.60</P_13_1>", "<P_13_1>50.00</P_13_1>", StringComparison.Ordinal);

        Assert.Contains(Fa3VatArithmetic.Check(Utf8(xml)), e => e.Message.Contains("P_13_1 = 50.00"));
    }

    [Fact]
    public void NonVatInvoiceKinds_AreSkipped()
    {
        string xml = ValidXml.Replace("<P_14_1>10.03</P_14_1>", "<P_14_1>99.00</P_14_1>", StringComparison.Ordinal)
            .Replace("<RodzajFaktury>VAT</RodzajFaktury>", "<RodzajFaktury>ZAL</RodzajFaktury>", StringComparison.Ordinal);

        Assert.Empty(Fa3VatArithmetic.Check(Utf8(xml)));
    }

    [Fact]
    public void MfSamples_HaveNoFalseAlarms()
    {
        foreach (string template in new[] { "invoice-template-fa-3-with-custom-Subject3.xml", "invoice-template-fa-3-with-attachment.xml", "invoice-template-fa-3-with-custom-Subject2.xml" })
        {
            Assert.Empty(Fa3VatArithmetic.Check(Utf8(TestInvoices.Fill(template))));
        }
    }

    private static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);
}
