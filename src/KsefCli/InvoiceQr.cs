using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using KSeF.Client.Api.Services;
using KSeF.Client.DI;
using KSeF.Client.Validation;
using QRCoder;

namespace KsefCli;

/// <summary>
/// Kod weryfikujący fakturę (KOD I, § 9 rozporządzenia w sprawie korzystania z KSeF): link do KSeF z NIP-em
/// sprzedawcy, datą wystawienia (P_1) i skrótem SHA-256 pliku, obowiązkowy na fakturze przekazanej poza KSeF
/// (konsument, nabywca zagraniczny, tryb offline). Pod kodem numer KSeF albo „OFFLINE”, gdy faktura go nie ma.
/// Skrót liczony z bajtów pliku — po wysłaniu do KSeF pliku nie wolno przeformatowywać.
/// </summary>
internal static partial class InvoiceQr
{
    private static readonly XNamespace Fa3 = "http://crd.gov.pl/wzor/2025/06/25/13775/";

    public static string Url(KsefEnvironment env, byte[] xml)
    {
        (string nip, DateTime issueDate) = SellerAndIssueDate(xml);
        VerificationLinkService links = new(new KSeFClientOptions { BaseUrl = env.ApiUrl, BaseQRUrl = env.QrUrl });
        return links.BuildInvoiceVerificationUrl(nip, issueDate, Convert.ToBase64String(SHA256.HashData(xml)));
    }

    public static bool IsKsefNumber(string value) => KsefNumberRegex().IsMatch(value);

    /// <summary>Blok HTML z kodem (SVG w data URI — poprawny w HTML 4.01 wizualizacji MF), podpisem i linkiem.</summary>
    /// <remarks>Poza produkcją pod kodem widoczna adnotacja o środowisku — wydruk z linkiem do TEST/DEMO nie nadaje się dla nabywcy.</remarks>
    public static string HtmlBlock(string url, string label, KsefEnvironment env)
    {
        string environmentNote = env.Name == "prod"
            ? ""
            : $"<div style=\"color:#b00;font-weight:bold\">KSeF {env.Name.ToUpperInvariant()} — nie dla faktur produkcyjnych (użyj -e prod)</div>";
        string svg = Convert.ToBase64String(Encoding.UTF8.GetBytes(Svg(url)));
        string href = WebUtility.HtmlEncode(url);
        return "<div class=\"ksef-kod-weryfikujacy\" style=\"float:right;margin:0 0 1em 1em;text-align:center;font-family:sans-serif;font-size:9pt\">"
            + $"<img src=\"data:image/svg+xml;base64,{svg}\" alt=\"Kod QR weryfikujący fakturę w KSeF\" style=\"width:40mm;height:40mm\">"
            + $"<div style=\"font-weight:bold\">{WebUtility.HtmlEncode(label)}</div>" + environmentNote
            + $"<div style=\"max-width:60mm;word-break:break-all\"><a href=\"{href}\">{href}</a></div></div>";
    }

    private static string Svg(string url)
    {
        using QRCodeGenerator generator = new();
        using QRCodeData data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        int size = data.ModuleMatrix.Count;
        StringBuilder path = new();
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (data.ModuleMatrix[y][x])
                {
                    path.Append(CultureInfo.InvariantCulture, $"M{x},{y}h1v1h-1z");
                }
            }
        }
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {size} {size}\" shape-rendering=\"crispEdges\">"
            + $"<rect width=\"{size}\" height=\"{size}\" fill=\"#fff\"/><path fill=\"#000\" d=\"{path}\"/></svg>";
    }

    /// <summary>NIP sprzedawcy (Podmiot1) i data wystawienia (P_1) — także do kontroli przed wysyłką.</summary>
    public static (string Nip, DateTime IssueDate) SellerAndIssueDate(byte[] xml)
    {
        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using XmlReader reader = XmlReader.Create(new MemoryStream(xml), settings);
        XElement root = XDocument.Load(reader).Root
            ?? throw new XmlException("pusty dokument.");
        string? nip = root.Element(Fa3 + "Podmiot1")?.Element(Fa3 + "DaneIdentyfikacyjne")?.Element(Fa3 + "NIP")?.Value;
        string? date = root.Element(Fa3 + "Fa")?.Element(Fa3 + "P_1")?.Value;
        if (nip is null || date is null)
        {
            throw new XmlException("do kodu QR potrzebne są NIP sprzedawcy (Podmiot1) i data wystawienia (P_1) faktury FA(3).");
        }
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime issueDate))
        {
            throw new XmlException($"niepoprawna data wystawienia P_1: {date}.");
        }
        return (nip, issueDate);
    }

    [GeneratedRegex(RegexPatterns.KsefNumberPattern, RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex KsefNumberRegex();
}
