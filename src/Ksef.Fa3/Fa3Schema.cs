using System.Xml;
using System.Xml.Schema;

namespace Ksef.Fa3;

/// <summary>Błąd walidacji z pozycją w pliku. Source: "XSD" (schemat FA(3)) albo "KSeF" (wymagania KSeF ponad schemat).</summary>
public sealed record Fa3ValidationError(XmlSeverityType Severity, int Line, int Column, string Message, string Source = "XSD")
{
    public override string ToString() =>
        $"{Line}:{Column} {Source} {(Severity == XmlSeverityType.Error ? "błąd" : "ostrzeżenie")}: {Message}";
}

/// <summary>Walidacja dokumentu względem oficjalnego schematu FA(3) wbudowanego w bibliotekę.</summary>
public static class Fa3Schema
{
    public const string Namespace = "http://crd.gov.pl/wzor/2025/06/25/13775/";

    private const string ResourcePrefix = "Ksef.Fa3.Schemas.";

    private static readonly Lazy<XmlSchemaSet> Schemas = new(LoadSchemas);

    public static IReadOnlyList<Fa3ValidationError> Validate(Stream xml)
    {
        List<Fa3ValidationError> errors = [];
        XmlReaderSettings settings = new()
        {
            ValidationType = ValidationType.Schema,
            Schemas = Schemas.Value,
            ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };
        settings.ValidationEventHandler += (_, e) =>
            errors.Add(new Fa3ValidationError(e.Severity, e.Exception.LineNumber, e.Exception.LinePosition, e.Message));

        using XmlReader reader = XmlReader.Create(xml, settings);
        try
        {
            reader.MoveToContent();
            if (reader.NamespaceURI != Namespace || reader.LocalName != "Faktura")
            {
                IXmlLineInfo info = (IXmlLineInfo)reader;
                errors.Add(new Fa3ValidationError(XmlSeverityType.Error, info.LineNumber, info.LinePosition,
                    $"Element główny to {{{reader.NamespaceURI}}}{reader.LocalName}, oczekiwano Faktura w przestrzeni FA(3) {Namespace}."));
                return errors;
            }
            while (reader.Read())
            {
            }
        }
        catch (XmlException ex)
        {
            errors.Add(new Fa3ValidationError(XmlSeverityType.Error, ex.LineNumber, ex.LinePosition, ex.Message));
        }
        return errors;
    }

    private static XmlSchemaSet LoadSchemas()
    {
        System.Reflection.Assembly assembly = typeof(Fa3Schema).Assembly;
        XmlSchemaSet set = new() { XmlResolver = new EmbeddedResourceResolver(assembly, ResourcePrefix) };
        using Stream root = assembly.GetManifestResourceStream(ResourcePrefix + "schemat.xsd")
            ?? throw new InvalidOperationException("Brak wbudowanego schematu FA(3).");
        using XmlReader reader = XmlReader.Create(root, new XmlReaderSettings { XmlResolver = null }, ResourcePrefix + "schemat.xsd");
        set.Add(XmlSchema.Read(reader, null)!);
        set.Compile();
        return set;
    }
}
