using System.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public static void WriteJson<T>(T value) => Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    public static void Error(string message) => Console.Error.WriteLine($"ksef: {message}");
}
