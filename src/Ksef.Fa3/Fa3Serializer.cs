using System.Text;
using System.Xml;
using System.Xml.Serialization;

namespace Ksef.Fa3;

/// <summary>Odczyt i zapis faktury FA(3) do/z wygenerowanych klas.</summary>
public static class Fa3Serializer
{
    private static readonly XmlSerializer Serializer = new(typeof(Faktura));

    public static Faktura Load(Stream xml)
    {
        using XmlReader reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return (Faktura)Serializer.Deserialize(reader)!;
    }

    public static void Save(Faktura invoice, Stream output)
    {
        XmlWriterSettings settings = new() { Encoding = new UTF8Encoding(false), Indent = true };
        using XmlWriter writer = XmlWriter.Create(output, settings);
        XmlSerializerNamespaces namespaces = new();
        namespaces.Add("", Fa3Schema.Namespace);
        Serializer.Serialize(writer, invoice, namespaces);
    }
}
