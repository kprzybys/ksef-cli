using System.CommandLine;
using System.Runtime.Versioning;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

// Narzędzie dla Linuksa: prawa plików (0600/0700), $XDG_RUNTIME_DIR.
[assembly: SupportedOSPlatform("linux")]

namespace KsefCli;

/// <summary>
/// Wspólne zasady wejścia/wyjścia komend: `-` oznacza stdin/stdout, wynik idzie na stdout,
/// diagnostyka na stderr, `--json` daje wynik dla skryptów.
/// </summary>
public static class CliIo
{
    public const string StdStream = "-";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Polskie znaki wprost, nie jako \uXXXX — wynik czyta też człowiek, a jq radzi sobie z obydwoma.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Option<bool> JsonOption() => new("--json")
    {
        Description = "Wynik w JSON na stdout (dla skryptów)",
    };

    /// <summary>Nazwa wejścia w komunikatach: ścieżka jak podana albo `<stdin>`.</summary>
    public static string DisplayName(string path) => path == StdStream ? "<stdin>" : path;

    public static byte[] ReadAllBytes(string path)
    {
        if (path != StdStream)
        {
            return File.ReadAllBytes(path);
        }
        using Stream stdin = Console.OpenStandardInput();
        using MemoryStream buffer = new();
        stdin.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Zapisuje gotowy wynik naraz, żeby błąd w trakcie nie zostawił połowy pliku.</summary>
    public static void WriteAllBytes(string path, byte[] content)
    {
        if (path != StdStream)
        {
            File.WriteAllBytes(path, content);
            return;
        }
        using Stream stdout = Console.OpenStandardOutput();
        stdout.Write(content);
    }

    /// <summary>
    /// Identyfikatory z argumentów albo — gdy brak lub `-` — ze stdin, po jednym w wierszu; z wiersza bierzemy
    /// pierwszą kolumnę TSV, więc wynik `ksef list` / `ksef send` można przekazać dalej bez `cut`.
    /// </summary>
    public static IReadOnlyList<string> ReadIdentifiers(string[] args)
    {
        if (args is not [] and not [StdStream])
        {
            return args;
        }
        List<string> identifiers = [];
        while (Console.In.ReadLine() is { } line)
        {
            string first = line.Split('\t', 2)[0].Trim();
            if (first.Length > 0)
            {
                identifiers.Add(first);
            }
        }
        return identifiers;
    }

    /// <summary>Zapis przez plik tymczasowy i zmianę nazwy — przerwany zapis nie zostawia uciętego pliku.</summary>
    public static void WriteFileAtomically(string path, byte[] content)
    {
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    public static void WriteJson<T>(T value) => Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    public static void Error(string message) => Console.Error.WriteLine($"ksef: {message}");
}
