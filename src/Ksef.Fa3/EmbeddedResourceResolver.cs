using System.Reflection;
using System.Xml;

namespace Ksef.Fa3;

/// <summary>
/// Rozwiązuje odwołania (schemaLocation, xsl:import, document()) po samej nazwie pliku
/// na zasoby wbudowane w bibliotekę. Nigdy nie sięga do sieci ani na dysk.
/// </summary>
internal sealed class EmbeddedResourceResolver(Assembly assembly, string resourcePrefix) : XmlResolver
{
    public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
    {
        string name = resourcePrefix + Path.GetFileName(absoluteUri.LocalPath);
        return assembly.GetManifestResourceStream(name)
            ?? throw new XmlException($"Plik {absoluteUri} nie jest wbudowany (oczekiwano zasobu {name}).");
    }

    public override Uri ResolveUri(Uri? baseUri, string? relativeUri) =>
        new($"embedded:///{Path.GetFileName(relativeUri ?? "")}");
}
