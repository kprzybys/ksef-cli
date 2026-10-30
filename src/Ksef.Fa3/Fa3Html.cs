using System.Xml;

namespace Ksef.Fa3;

/// <summary>
/// Wizualizacja faktury FA(3) do HTML oficjalnym arkuszem MF (crd.gov.pl, styl.xsl, XSLT 1.0), offline.
/// Przekształca libxslt, nie XslCompiledTransform: ten kompiluje arkusz do IL przy każdym uruchomieniu
/// (ponad sekunda) i nie działa w Native AOT.
/// </summary>
public static class Fa3Html
{
    private const string ResourcePrefix = "Ksef.Fa3.Stylesheets.";

    public static void Render(Stream xml, Stream html)
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
            html.Write(LibXslt.Transform(Path.Combine(dir.FullName, "styl.xsl"), input.ToArray()));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
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
