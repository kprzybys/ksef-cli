using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Xsl;

namespace Ksef.Fa3;

/// <summary>Minimalne wiązanie do libxslt (pakiet libxslt1.1): arkusz z pliku, dokument z pamięci, wynik wg xsl:output.</summary>
internal static unsafe partial class LibXslt
{
    private const string Xml2 = "libxml2.so.2";
    private const string Xslt = "libxslt.so.1";

    // Dokument wejściowy nigdy nie sięga do sieci; bez NOENT/DTDLOAD encje zewnętrzne nie są ładowane.
    private const int XmlParseNoNet = 1 << 11;

    public static byte[] Transform(string stylesheetPath, byte[] input)
    {
        try
        {
            return TransformCore(stylesheetPath, input);
        }
        catch (DllNotFoundException ex)
        {
            throw new XsltException($"brak biblioteki libxslt — zainstaluj pakiet libxslt1.1 ({ex.Message})");
        }
    }

    private static byte[] TransformCore(string stylesheetPath, byte[] input)
    {
        nint style = xsltParseStylesheetFile(stylesheetPath);
        if (style == 0)
        {
            throw new XsltException($"libxslt nie wczytał arkusza {Path.GetFileName(stylesheetPath)}.");
        }
        nint doc = 0;
        nint result = 0;
        try
        {
            fixed (byte* bytes = input)
            {
                doc = xmlReadMemory(bytes, input.Length, "faktura.xml", null, XmlParseNoNet);
            }
            if (doc == 0)
            {
                throw new XmlException("libxml2 nie wczytał dokumentu.");
            }
            result = xsltApplyStylesheet(style, doc, null);
            if (result == 0)
            {
                throw new XsltException("libxslt: przekształcenie nie powiodło się.");
            }
            if (xsltSaveResultToString(out byte* buffer, out int length, result, style) != 0)
            {
                throw new XsltException("libxslt: nie zapisano wyniku.");
            }
            try
            {
                return new ReadOnlySpan<byte>(buffer, length).ToArray();
            }
            finally
            {
                XmlFree(buffer);
            }
        }
        finally
        {
            if (result != 0)
            {
                xmlFreeDoc(result);
            }
            if (doc != 0)
            {
                xmlFreeDoc(doc);
            }
            xsltFreeStylesheet(style);
        }
    }

    // xmlFree to w libxml2 zmienna globalna ze wskaźnikiem na funkcję zwalniającą, nie funkcja.
    private static void XmlFree(void* pointer)
    {
        if (pointer != null)
        {
            var free = *(delegate* unmanaged<void*, void>*)NativeLibrary.GetExport(NativeLibrary.Load(Xml2), "xmlFree");
            free(pointer);
        }
    }

    [LibraryImport(Xslt, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint xsltParseStylesheetFile(string filename);

    [LibraryImport(Xslt)]
    private static partial nint xsltApplyStylesheet(nint style, nint doc, byte** parameters);

    [LibraryImport(Xslt)]
    private static partial int xsltSaveResultToString(out byte* buffer, out int length, nint result, nint style);

    [LibraryImport(Xslt)]
    private static partial void xsltFreeStylesheet(nint style);

    [LibraryImport(Xml2, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint xmlReadMemory(byte* buffer, int size, string url, string? encoding, int options);

    [LibraryImport(Xml2)]
    private static partial void xmlFreeDoc(nint doc);
}
