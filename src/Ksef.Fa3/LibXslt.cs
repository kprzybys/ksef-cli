using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Xsl;

namespace Ksef.Fa3;

/// <summary>Minimalne wiązanie do libxslt (pakiet libxslt1.1): arkusz z pliku, dokument z pamięci, wynik wg xsl:output.</summary>
internal static unsafe partial class LibXslt
{
    private const string Xslt = "libxslt.so.1";

    // Dokument wejściowy nigdy nie sięga do sieci; bez NOENT/DTDLOAD encje zewnętrzne nie są ładowane.
    private const int XmlParseNoNet = 1 << 11;

    public static byte[] Transform(string stylesheetPath, byte[] input)
    {
        try
        {
            return TransformCore(stylesheetPath, input);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new XsltException($"brak biblioteki libxslt — zainstaluj pakiet libxslt1.1 (Fedora/RHEL/Arch: libxslt) ({ex.Message})", ex);
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
            var readMemory = (delegate* unmanaged<byte*, int, byte*, byte*, int, nint>)Xml2("xmlReadMemory");
            fixed (byte* bytes = input)
            fixed (byte* url = "faktura.xml\0"u8)
            {
                doc = readMemory(bytes, input.Length, url, null, XmlParseNoNet);
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
            var freeDoc = (delegate* unmanaged<nint, void>)Xml2("xmlFreeDoc");
            if (result != 0)
            {
                freeDoc(result);
            }
            if (doc != 0)
            {
                freeDoc(doc);
            }
            xsltFreeStylesheet(style);
        }
    }

    // xmlFree to w libxml2 zmienna globalna ze wskaźnikiem na funkcję zwalniającą, nie funkcja.
    private static void XmlFree(void* pointer)
    {
        if (pointer != null)
        {
            var free = *(delegate* unmanaged<void*, void>*)Xml2("xmlFree");
            free(pointer);
        }
    }

    // libxml2 przez uchwyt libxslt: dlsym przeszukuje też jej zależności, więc dostajemy tę libxml2, z którą
    // zbudowano libxslt — libxml2.so.2, a od libxml2 2.14 (np. Ubuntu 26.04) libxml2.so.16 — bez zgadywania nazwy.
    private static nint Xml2(string name) => NativeLibrary.GetExport(NativeLibrary.Load(Xslt), name);

    [LibraryImport(Xslt, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint xsltParseStylesheetFile(string filename);

    [LibraryImport(Xslt)]
    private static partial nint xsltApplyStylesheet(nint style, nint doc, byte** parameters);

    [LibraryImport(Xslt)]
    private static partial int xsltSaveResultToString(out byte* buffer, out int length, nint result, nint style);

    [LibraryImport(Xslt)]
    private static partial void xsltFreeStylesheet(nint style);
}
