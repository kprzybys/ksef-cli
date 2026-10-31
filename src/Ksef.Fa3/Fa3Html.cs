using System.Text;
using System.Xml;
using System.Xml.Xsl;

namespace Ksef.Fa3;

/// <summary>
/// Wizualizacja faktury FA(3) do HTML oficjalnym arkuszem MF (crd.gov.pl, styl.xsl, XSLT 1.0), offline.
/// Przekształca libxslt, nie XslCompiledTransform: ten kompiluje arkusz do IL przy każdym uruchomieniu
/// (ponad sekunda) i nie działa w Native AOT.
/// </summary>
public static class Fa3Html
{
    private const string ResourcePrefix = "Ksef.Fa3.Stylesheets.";

    /// <param name="bodyPrefix">HTML wstawiany na początku &lt;body&gt; (np. kod QR weryfikujący fakturę); null — bez zmian.</param>
    public static void Render(Stream xml, Stream html, string? bodyPrefix = null)
    {
        using MemoryStream input = new();
        xml.CopyTo(input);
        EnsureWellFormedWithoutDtd(input.ToArray());

        // libxslt rozwiązuje xsl:import i document() (słowniki krajów, walut, urzędów) względem pliku
        // arkusza, więc wbudowane zasoby rozpakowujemy na czas przekształcenia do katalogu tymczasowego.
        DirectoryInfo dir = Directory.CreateTempSubdirectory("ksef-fa3-");
        try
        {
            ExtractStylesheets(dir.FullName);
            // Wynik bez BOM: idzie też na stdout, gdzie BOM psuje potoki (grep, sklejanie plików).
            byte[] result = LibXslt.Transform(Path.Combine(dir.FullName, "styl.xsl"), input.ToArray());
            html.Write(bodyPrefix is null ? result : InsertAfterBodyTag(result, bodyPrefix));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // Arkusz pisze UTF-8 (xsl:output encoding="UTF-8"); wstawka zaraz za znacznikiem otwierającym <body>.
    private static byte[] InsertAfterBodyTag(byte[] html, string fragment)
    {
        string text = Encoding.UTF8.GetString(html);
        int body = text.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        int end = body < 0 ? -1 : text.IndexOf('>', body);
        if (end < 0)
        {
            throw new XsltException("wynik arkusza nie ma znacznika <body>.");
        }
        return Encoding.UTF8.GetBytes(text.Insert(end + 1, fragment));
    }

    // Te same zasady co przy walidacji: bez DTD i encji zewnętrznych; błąd składni z numerem wiersza.
    private static void EnsureWellFormedWithoutDtd(byte[] xml)
    {
        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using XmlReader reader = XmlReader.Create(new MemoryStream(xml), settings);
        while (reader.Read())
        {
        }
    }

    private static void ExtractStylesheets(string directory)
    {
        System.Reflection.Assembly assembly = typeof(Fa3Html).Assembly;
        foreach (string name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using Stream resource = assembly.GetManifestResourceStream(name)!;
            using FileStream file = File.Create(Path.Combine(directory, name[ResourcePrefix.Length..]));
            resource.CopyTo(file);
        }
    }
}
