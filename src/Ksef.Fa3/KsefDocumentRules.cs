using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Schema;

namespace Ksef.Fa3;

/// <summary>
/// Wymagania KSeF wobec pliku faktury, których nie sprawdza XSD
/// (https://github.com/CIRFMF/ksef-api/blob/main/faktury/weryfikacja-faktury.md): UTF-8 bez BOM, prolog bez innego kodowania,
/// brak instrukcji przetwarzania, brak niezalecanych znaków Unicode, limit rozmiaru, sumy kontrolne NIP
/// (te KSeF sprawdza tylko na produkcji — na TEST faktura z błędnym NIP-em przechodzi).
/// </summary>
public static partial class KsefDocumentRules
{
    public const int MaxSizeWithoutAttachment = 1_000_000;
    public const int MaxSizeWithAttachment = 3_000_000;

    private const string Source = "KSeF";

    // Niezalecane znaki XML 1.0 (https://www.w3.org/TR/xml/#charsets), które KSeF odrzuca.
    private static readonly (int From, int To)[] DiscouragedRanges = BuildDiscouragedRanges();

    public static IReadOnlyList<Fa3ValidationError> Check(byte[] xml)
    {
        List<Fa3ValidationError> errors = [];

        if (xml.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            errors.Add(Error(1, 1, "Plik zaczyna się od znaku BOM — KSeF wymaga UTF-8 bez BOM."));
        }

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(xml);
        }
        catch (DecoderFallbackException ex)
        {
            errors.Add(Error(1, 1, $"Plik nie jest poprawnym UTF-8 (bajt na pozycji {ex.Index})."));
            return errors;
        }

        CheckDeclaredEncoding(text, errors);
        CheckDiscouragedCharacters(text, errors);
        bool hasAttachment = CheckStructure(xml, errors);
        CheckSize(xml.Length, hasAttachment, errors);
        return errors;
    }

    private static void CheckDeclaredEncoding(string text, List<Fa3ValidationError> errors)
    {
        Match declaration = XmlDeclaration().Match(text);
        if (declaration.Success && declaration.Groups["enc"].Success
            && !declaration.Groups["enc"].Value.Equals("UTF-8", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(Error(1, 1, $"Prolog deklaruje kodowanie {declaration.Groups["enc"].Value} — dozwolone jest tylko UTF-8."));
        }
    }

    private static void CheckDiscouragedCharacters(string text, List<Fa3ValidationError> errors)
    {
        int line = 1, column = 0;
        for (int i = 0; i < text.Length; i++)
        {
            int codePoint = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? char.ConvertToUtf32(text[i], text[++i]) : text[i];
            column++;
            if (codePoint == '\n')
            {
                line++;
                column = 0;
                continue;
            }
            if (IsDiscouraged(codePoint))
            {
                errors.Add(Error(line, column, $"Niedozwolony znak Unicode U+{codePoint:X4}."));
            }
        }
    }

    /// <summary>Szuka instrukcji przetwarzania i załącznika. Zwraca, czy faktura ma załącznik.</summary>
    private static bool CheckStructure(byte[] xml, List<Fa3ValidationError> errors)
    {
        bool hasAttachment = false;
        (int Line, int Column)? nip = null;
        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using XmlReader reader = XmlReader.Create(new MemoryStream(xml), settings);
        try
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.ProcessingInstruction)
                {
                    IXmlLineInfo info = (IXmlLineInfo)reader;
                    errors.Add(Error(info.LineNumber, info.LinePosition, $"Instrukcja przetwarzania <?{reader.Name} ...?> jest niedozwolona."));
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Zalacznik" && reader.NamespaceURI == Fa3Schema.Namespace)
                {
                    hasAttachment = true;
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "NIP" && reader.NamespaceURI == Fa3Schema.Namespace)
                {
                    IXmlLineInfo info = (IXmlLineInfo)reader;
                    nip = (info.LineNumber, info.LinePosition);
                }
                else if (nip is { } at && reader.NodeType == XmlNodeType.Text)
                {
                    // Wzorzec NIP-u (10 cyfr) sprawdza XSD; tu tylko suma kontrolna poprawnie zbudowanego.
                    string value = reader.Value.Trim();
                    if (value.Length == 10 && value.All(char.IsAsciiDigit) && !IsNipChecksumValid(value))
                    {
                        errors.Add(Error(at.Line, at.Column, $"NIP {value} ma błędną sumę kontrolną — KSeF produkcyjny odrzuci fakturę (na TEST przejdzie)."));
                    }
                    nip = null;
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    nip = null;
                }
            }
        }
        catch (XmlException)
        {
            // Błąd składni zgłasza walidacja XSD (z pozycją); tu nie dublujemy.
        }
        return hasAttachment;
    }

    // Wagi 6,5,7,2,3,4,5,6,7; reszta z dzielenia przez 11 to ostatnia cyfra (10 — NIP niepoprawny).
    private static bool IsNipChecksumValid(string nip)
    {
        ReadOnlySpan<int> weights = [6, 5, 7, 2, 3, 4, 5, 6, 7];
        int sum = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            sum += (nip[i] - '0') * weights[i];
        }
        return sum % 11 == nip[9] - '0';
    }

    private static void CheckSize(int size, bool hasAttachment, List<Fa3ValidationError> errors)
    {
        int limit = hasAttachment ? MaxSizeWithAttachment : MaxSizeWithoutAttachment;
        if (size > limit)
        {
            errors.Add(Error(1, 1, $"Rozmiar {size:N0} B przekracza limit {limit:N0} B dla faktury {(hasAttachment ? "z załącznikiem" : "bez załącznika")}."));
        }
    }

    private static bool IsDiscouraged(int codePoint)
    {
        foreach ((int from, int to) in DiscouragedRanges)
        {
            if (codePoint >= from && codePoint <= to)
            {
                return true;
            }
        }
        return false;
    }

    private static (int, int)[] BuildDiscouragedRanges()
    {
        List<(int, int)> ranges = [(0x7F, 0x84), (0x86, 0x9F), (0xFDD0, 0xFDEF)];
        for (int plane = 1; plane <= 0x10; plane++)
        {
            ranges.Add(((plane << 16) | 0xFFFE, (plane << 16) | 0xFFFF));
        }
        return [.. ranges];
    }

    private static Fa3ValidationError Error(int line, int column, string message) =>
        new(XmlSeverityType.Error, line, column, message, Source);

    [GeneratedRegex("""^<\?xml\s[^?]*?encoding\s*=\s*["'](?<enc>[^"']+)["']""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex XmlDeclaration();
}
