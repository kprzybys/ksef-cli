using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KsefCli.Tests;

/// <summary>
/// `ksef show` z kodem weryfikującym fakturę (KOD I): link wg dokumentacji MF —
/// `{host}/invoice/{NIP sprzedawcy}/{P_1 jako DD-MM-RRRR}/{SHA-256 pliku, Base64URL}` — i podpis pod kodem.
/// </summary>
public class ShowQrTests : IDisposable
{
    private const string KsefNumber = "1111111111-20260201-0123456789AB-CD";

    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    private static string ExpectedUrl(string host, byte[] xml)
    {
        string hash = Convert.ToBase64String(SHA256.HashData(xml)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        // Wzór MF: sprzedawca 1111111111, P_1 = 2025-09-01.
        return $"{host}/invoice/1111111111/01-09-2025/{hash}";
    }

    private static string QrBlock(string html) =>
        Regex.Match(html, "<div class=\"ksef-kod-weryfikujacy\".*?</div></div>", RegexOptions.Singleline).Value;

    [Fact]
    public async Task Show_Prod_LinksToProductionVerificationWithKsefNumber()
    {
        byte[] xml = TestData.ValidInvoice();

        CliResult result = await Ksef.RunAsync(dir.Path, xml, "show", "-e", "prod", "--ksef-number", KsefNumber);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
        string block = QrBlock(result.Stdout);
        Assert.Contains($"href=\"{ExpectedUrl("https://qr.ksef.mf.gov.pl", xml)}\"", block);
        Assert.Contains($">{KsefNumber}<", block);
        Assert.DoesNotContain("nie dla faktur produkcyjnych", block);
        // Kod jako SVG w data URI — sam obraz (moduły QR) z biblioteki QRCoder.
        string svg = Encoding.UTF8.GetString(Convert.FromBase64String(
            Regex.Match(block, "data:image/svg\\+xml;base64,([^\"]+)").Groups[1].Value));
        Assert.StartsWith("<svg", svg);
        Assert.Contains("<path fill=\"#000\" d=\"M", svg);
    }

    [Fact]
    public async Task Show_WithoutKsefNumber_IsLabelledOffline_AndMarksTestEnvironment()
    {
        byte[] xml = TestData.ValidInvoice();

        CliResult result = await Ksef.RunAsync(dir.Path, xml, "show");

        Assert.Equal(0, result.ExitCode);
        string block = QrBlock(result.Stdout);
        Assert.Contains($"href=\"{ExpectedUrl("https://qr-test.ksef.mf.gov.pl", xml)}\"", block);
        Assert.Contains(">OFFLINE<", block);
        Assert.Contains("KSeF TEST — nie dla faktur produkcyjnych", block);
    }

    [Fact]
    public async Task Show_HashCoversExactFileBytes()
    {
        // Ta sama faktura, inne białe znaki — inny plik, więc inny skrót w linku.
        byte[] xml = TestData.ValidInvoice();
        byte[] reformatted = [.. xml, .. "\n"u8.ToArray()];

        CliResult a = await Ksef.RunAsync(dir.Path, xml, "show", "-e", "prod");
        CliResult b = await Ksef.RunAsync(dir.Path, reformatted, "show", "-e", "prod");

        Assert.Contains(ExpectedUrl("https://qr.ksef.mf.gov.pl", reformatted), b.Stdout);
        Assert.NotEqual(QrBlock(a.Stdout), QrBlock(b.Stdout));
    }

    [Fact]
    public async Task Show_NoQr_OmitsVerificationBlock()
    {
        CliResult result = await Ksef.RunAsync(dir.Path, TestData.ValidInvoice(), "show", "--no-qr");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("ksef-kod-weryfikujacy", result.Stdout);
    }

    [Fact]
    public async Task Show_InvalidKsefNumber_FailsWithoutOutput()
    {
        CliResult result = await Ksef.RunAsync(dir.Path, TestData.ValidInvoice(), "show", "--ksef-number", "123");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdoutBytes);
        Assert.Equal("ksef: niepoprawny numer KSeF: 123\n", result.Stderr);
    }
}
