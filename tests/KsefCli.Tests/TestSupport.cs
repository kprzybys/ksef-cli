using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

// Narzędzie jest dla Linuksa: testy sprawdzają uprawnienia plików i używają `script`.
[assembly: SupportedOSPlatform("linux")]
[assembly: SupportedOSPlatform("macos")]

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

    public static Task<CliResult> RunAsync(string runtimeDir, byte[]? stdin, params string[] args) => RunAsync(runtimeDir, stdin, null, args);

    public static Task<CliResult> RunAsync(string runtimeDir, byte[]? stdin, IReadOnlyDictionary<string, string>? environment, params string[] args) =>
        RunCoreAsync(runtimeDir, stdin, environment, null, args);

    /// <summary>Jak RunAsync, ale po `after` wysyła SIGINT (jak Ctrl+C albo `timeout -s INT` w cronie) — do testów przerwania.</summary>
    public static Task<CliResult> RunInterruptedAsync(string runtimeDir, TimeSpan after, IReadOnlyDictionary<string, string>? environment, params string[] args) =>
        RunCoreAsync(runtimeDir, null, environment, after, args);

    private static async Task<CliResult> RunCoreAsync(string runtimeDir, byte[]? stdin, IReadOnlyDictionary<string, string>? environment, TimeSpan? interruptAfter, params string[] args)
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
        // KSEF_ENV/KSEF_API_URL z powłoki uruchamiającej testy (np. KSEF_ENV=prod w ~/.bashrc) skierowałyby testy
        // na produkcję albo obcy adres; test, który ich potrzebuje, podaje je jawnie w `environment`.
        start.Environment.Remove("KSEF_ENV");
        start.Environment.Remove("KSEF_API_URL");
        foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Nie uruchomiono ksef.");
        Task interrupt = interruptAfter is { } after ? InterruptAsync(process, after) : Task.CompletedTask;
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using MemoryStream stdout = new();
        Task copyStdout = process.StandardOutput.BaseStream.CopyToAsync(stdout);

        // ksef, który kończy się przed odczytem stdin (np. błąd katalogu sesji), zamyka rurę — wtedy zapis i jej
        // zamknięcie dają „Broken pipe”, co nie jest błędem testu (szybka binarka AOT trafia na to częściej).
        try
        {
            if (stdin is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(stdin);
            }
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        await process.WaitForExitAsync(timeout.Token);
        await copyStdout;
        await interrupt;
        return new CliResult(process.ExitCode, stdout.ToArray(), await stderr);
    }

    // `kill` zamiast `timeout` z coreutils — tego drugiego nie ma na macOS.
    private static async Task InterruptAsync(Process process, TimeSpan after)
    {
        await Task.Delay(after);
        if (!process.HasExited)
        {
            using Process kill = Process.Start("kill", ["-INT", process.Id.ToString(CultureInfo.InvariantCulture)]);
            await kill.WaitForExitAsync();
        }
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
