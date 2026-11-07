using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Ksef.Fa3;

/// <summary>
/// Kontrola arytmetyki VAT faktury (offline): sumy podstawy <c>P_13_x</c> względem wierszy (<c>P_11</c>) i kwota podatku
/// <c>P_14_x</c> względem <c>P_13_x</c> × stawka (albo sumy <c>P_11Vat</c> z wierszy), z tolerancją 1 gr.
/// Zakres zachowawczy, by nie dawać fałszywych alarmów: tylko <c>RodzajFaktury</c> = VAT (zaliczkowe, rozliczeniowe
/// i korygujące mają inną semantykę — pomijane), tylko grupy stawek 23/22, 8/7 i 5 (P_13_1..3 / P_14_1..3);
/// grupa jest pomijana, gdy brakuje danych do porównania. <c>P_15</c> nie jest sprawdzane.
/// </summary>
public static class Fa3VatArithmetic
{
    private const string Source = "KSeF";
    private const decimal Tolerance = 0.01m;

    // Numer grupy → dopuszczalne stawki P_12 (23/22 i 8/7 to stawki obowiązujące w różnych okresach).
    private static readonly (int Group, string[] Rates)[] Groups =
    [
        (1, ["23", "22"]),
        (2, ["8", "7"]),
        (3, ["5"]),
    ];

    public static IReadOnlyList<Fa3ValidationError> Check(byte[] xml)
    {
        XDocument doc;
        try
        {
            XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using XmlReader reader = XmlReader.Create(new MemoryStream(xml), settings);
            doc = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return []; // błąd składni zgłasza walidacja XSD
        }

        XNamespace ns = Fa3Schema.Namespace;
        XElement? fa = doc.Root?.Element(ns + "Fa");
        if (fa is null || fa.Element(ns + "RodzajFaktury")?.Value.Trim() != "VAT")
        {
            return [];
        }

        List<XElement> rows = [.. fa.Elements(ns + "FaWiersz")];
        List<Fa3ValidationError> errors = [];
        foreach ((int group, string[] rates) in Groups)
        {
            XElement? p13 = fa.Element(ns + $"P_13_{group}");
            XElement? p14 = fa.Element(ns + $"P_14_{group}");
            if (p13 is null || Parse(p13) is not { } net)
            {
                continue;
            }

            List<XElement> groupRows = [.. rows.Where(r => rates.Contains(r.Element(ns + "P_12")?.Value.Trim()))];
            if (groupRows.Count == 0)
            {
                continue;
            }

            if (Sum(groupRows, ns + "P_11") is { } rowsNet && Math.Abs(rowsNet - net) > Tolerance)
            {
                errors.Add(Error(p13, $"P_13_{group} = {Format(net)} nie zgadza się z sumą P_11 wierszy ze stawką {string.Join("/", rates)}% = {Format(rowsNet)}."));
            }

            if (p14 is null || Parse(p14) is not { } tax)
            {
                continue;
            }
            string[] distinct = [.. groupRows.Select(r => r.Element(ns + "P_12")!.Value.Trim()).Distinct(StringComparer.Ordinal)];
            if (distinct.Length != 1)
            {
                continue; // mieszane stawki w grupie (np. 23 i 22) — nie da się jednoznacznie wyliczyć
            }

            decimal expected = Math.Round(net * decimal.Parse(distinct[0], CultureInfo.InvariantCulture) / 100m, 2, MidpointRounding.AwayFromZero);
            decimal? rowsTax = Sum(groupRows, ns + "P_11Vat");
            if (Math.Abs(tax - expected) > Tolerance && (rowsTax is null || Math.Abs(tax - rowsTax.Value) > Tolerance))
            {
                errors.Add(Error(p14, $"P_14_{group} = {Format(tax)}, a podstawa P_13_{group} = {Format(net)} przy stawce {distinct[0]}% daje {Format(expected)}."
                    + (rowsTax is { } r ? $" Suma P_11Vat wierszy = {Format(r)}." : "")));
            }
        }
        return errors;
    }

    private static decimal? Parse(XElement e) =>
        decimal.TryParse(e.Value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal v) ? v : null;

    // Suma pola we wszystkich wierszach; null, gdy któryś wiersz go nie ma (nie ma z czym porównywać).
    private static decimal? Sum(List<XElement> rows, XName name)
    {
        decimal sum = 0;
        foreach (XElement row in rows)
        {
            if (row.Element(name) is not { } e || Parse(e) is not { } v)
            {
                return null;
            }
            sum += v;
        }
        return sum;
    }

    private static string Format(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static Fa3ValidationError Error(XElement at, string message)
    {
        IXmlLineInfo info = at;
        return new(XmlSeverityType.Error, info.LineNumber, info.LinePosition, message, Source);
    }
}
