using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

// Narzędzie jest dla Linuksa: testy sprawdzają uprawnienia plików i używają `script`.
[assembly: SupportedOSPlatform("linux")]

namespace KsefCli.Tests;

/// <summary>Wynik uruchomienia `ksef`: kod wyjścia, surowe bajty stdout (do sprawdzania BOM) i stderr.</summary>
internal sealed record CliResult(int ExitCode, byte[] StdoutBytes, string Stderr)
{
    public string Stdout => Encoding.UTF8.GetString(StdoutBytes);
}

/// <summary>
/// Uruchamia `ksef` jako proces z własnym, tymczasowym `XDG_RUNTIME_DIR`: zbudowany `ksef.dll`,
/// a gdy ustawiono `KSEF_BIN` — wskazaną binarkę (make test-aot: te same testy na Native AOT).
/// </summary>
internal static class Ksef
{
    private static readonly string Dll = Path.Combine(AppContext.BaseDirectory, "ksef.dll");

    // Ścieżka bezwzględna: testy działają w katalogu bin/ projektu testów.
    private static readonly string? Binary = Environment.GetEnvironmentVariable("KSEF_BIN") is { Length: > 0 } bin ? bin : null;

    /// <summary>Wiersz polecenia uruchamiający `ksef` (dla `script`, który potrzebuje jednego polecenia powłoki).</summary>
    public static string CommandLine => Binary is null ? $"dotnet {Dll}" : Binary;

    public static async Task<CliResult> RunAsync(string runtimeDir, byte[]? stdin, params string[] args)
    {
        ProcessStartInfo start = new(Binary ?? "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (Binary is null)
        {
            start.ArgumentList.Add(Dll);
        }
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        start.Environment["XDG_RUNTIME_DIR"] = runtimeDir;
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "pl";

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Nie uruchomiono ksef.");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using MemoryStream stdout = new();
        Task copyStdout = process.StandardOutput.BaseStream.CopyToAsync(stdout);

        if (stdin is not null)
        {
            await process.StandardInput.BaseStream.WriteAsync(stdin);
        }
        process.StandardInput.Close();

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        await process.WaitForExitAsync(timeout.Token);
        await copyStdout;
        return new CliResult(process.ExitCode, stdout.ToArray(), await stderr);
    }

    public static Task<CliResult> RunAsync(string runtimeDir, params string[] args) => RunAsync(runtimeDir, null, args);
}

/// <summary>Katalog tymczasowy usuwany po teście.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("ksef-tests-").FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal static class TestData
{
    /// <summary>Wzór MF FA(3) z wypełnionymi znacznikami — poprawny wobec XSD i reguł KSeF.</summary>
    public static byte[] ValidInvoice()
    {
        string template = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Templates", "invoice-template-fa-3.xml"));
        string filled = template.Replace("#nip#", "1111111111").Replace("#invoice_number#", "FV/TEST/1");
        Assert.DoesNotContain("#", filled.Replace("&#", ""));
        return new UTF8Encoding(false).GetBytes(filled);
    }

    /// <summary>
    /// Faktura do wysłania na KSeF TEST: sprzedawca = NIP sesji, unikalny numer (KSeF odrzuca duplikaty),
    /// opcjonalnie wskazany nabywca (wzór MF z `#nipOdbiorca#`).
    /// </summary>
    public static byte[] InvoiceToSend(string sellerNip, string? buyerNip = null)
    {
        string name = buyerNip is null ? "invoice-template-fa-3.xml" : "invoice-template-fa-3-with-custom-Subject2.xml";
        string xml = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Templates", name))
            .Replace("#nip#", sellerNip)
            .Replace("#invoice_number#", Guid.NewGuid().ToString("N")[..12])
            .Replace("#nipOdbiorca#", buyerNip ?? "");
        Assert.DoesNotContain("#", xml.Replace("&#", ""));
        return new UTF8Encoding(false).GetBytes(xml);
    }

    public static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>Losowy NIP z poprawną sumą kontrolną — na KSeF TEST każdy test dostaje własny kontekst.</summary>
    public static string RandomNip()
    {
        int[] weights = [6, 5, 7, 2, 3, 4, 5, 6, 7];
        while (true)
        {
            int[] digits = [Random.Shared.Next(1, 10), .. Enumerable.Range(0, 8).Select(_ => Random.Shared.Next(10))];
            int check = digits.Zip(weights, (d, w) => d * w).Sum() % 11;
            if (check < 10 && (digits[1], digits[2]) != (0, 0)) // wzorzec NIP w KSeF
            {
                return string.Concat(digits) + check;
            }
        }
    }
}

/// <summary>Testy zmieniające zmienną procesu `XDG_RUNTIME_DIR` nie mogą biec równolegle.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "XDG_RUNTIME_DIR";
}

/// <summary>Ustawia `XDG_RUNTIME_DIR` na katalog tymczasowy na czas testu i przywraca poprzednią wartość.</summary>
internal sealed class RuntimeDirScope : IDisposable
{
    private readonly string? previous = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
    private readonly TempDir dir = new();

    public RuntimeDirScope() => Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", dir.Path);

    public string Path => dir.Path;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previous);
        dir.Dispose();
    }
}
