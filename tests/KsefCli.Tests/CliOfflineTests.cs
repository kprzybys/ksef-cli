using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace KsefCli.Tests;

/// <summary>Zachowanie komend bez sieci: co idzie na stdout, co na stderr, kody wyjścia, stdin.</summary>
public class CliOfflineTests : IDisposable
{
    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    private string WriteFile(string name, byte[] content)
    {
        string path = dir.File(name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] WithBom(byte[] content) => [.. TestData.Bom, .. content];

    // --- validate ---

    [Fact]
    public async Task Validate_ValidFile_IsSilentAndSucceeds()
    {
        string file = WriteFile("ok.xml", TestData.ValidInvoice());

        CliResult result = await Ksef.RunAsync(dir.Path, "validate", file);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdoutBytes);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task Validate_Verbose_ListsValidFiles()
    {
        string file = WriteFile("ok.xml", TestData.ValidInvoice());

        CliResult result = await Ksef.RunAsync(dir.Path, "validate", "-v", file);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"{file}: OK\n", result.Stdout);
    }

    [Fact]
    public async Task Validate_InvalidFile_ReportsCompilerStyleErrorsOnStderr()
    {
        string file = WriteFile("bom.xml", WithBom(TestData.ValidInvoice()));

        CliResult result = await Ksef.RunAsync(dir.Path, "validate", file);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdoutBytes);
        Assert.StartsWith($"{file}:1:1: KSeF błąd: ", result.Stderr);
        Assert.Contains("BOM", result.Stderr);
    }

    [Fact]
    public async Task Validate_ReadsStdinWhenNoFileOrDash()
    {
        byte[] invalid = WithBom(TestData.ValidInvoice());

        CliResult implicitStdin = await Ksef.RunAsync(dir.Path, invalid, "validate");
        CliResult dash = await Ksef.RunAsync(dir.Path, TestData.ValidInvoice(), "validate", "-v", "-");

        Assert.Equal(1, implicitStdin.ExitCode);
        Assert.StartsWith("<stdin>:1:1:", implicitStdin.Stderr);
        Assert.Equal(0, dash.ExitCode);
        Assert.Equal("<stdin>: OK\n", dash.Stdout);
    }

    [Fact]
    public async Task Validate_MixedFiles_FailsAndReportsOnlyInvalidOne()
    {
        string ok = WriteFile("ok.xml", TestData.ValidInvoice());
        string bad = WriteFile("bad.xml", Encoding.UTF8.GetBytes("<Faktura/>"));

        CliResult result = await Ksef.RunAsync(dir.Path, "validate", ok, bad);

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain(ok, result.Stderr);
        Assert.Contains($"{bad}:", result.Stderr);
    }

    [Fact]
    public async Task Validate_Json_DescribesEveryFile()
    {
        string ok = WriteFile("ok.xml", TestData.ValidInvoice());
        string bad = WriteFile("bom.xml", WithBom(TestData.ValidInvoice()));

        CliResult result = await Ksef.RunAsync(dir.Path, "validate", "--json", ok, bad);

        Assert.Equal(1, result.ExitCode);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        JsonElement[] files = [.. json.RootElement.EnumerateArray()];
        Assert.Equal(2, files.Length);
        Assert.True(files[0].GetProperty("valid").GetBoolean());
        Assert.Empty(files[0].GetProperty("errors").EnumerateArray());
        Assert.False(files[1].GetProperty("valid").GetBoolean());
        JsonElement error = files[1].GetProperty("errors")[0];
        Assert.Equal("KSeF", error.GetProperty("source").GetString());
        Assert.Equal("Error", error.GetProperty("severity").GetString());
        Assert.Contains("BOM", error.GetProperty("message").GetString());
        Assert.Contains("się", result.Stdout); // polskie znaki wprost, nie \uXXXX
    }

    [Fact]
    public async Task Validate_MissingFile_ReportsAndFails()
    {
        string ok = WriteFile("ok.xml", TestData.ValidInvoice());
        string missing = dir.File("brak.xml");

        CliResult result = await Ksef.RunAsync(dir.Path, "validate", missing, ok);

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith($"ksef: {missing}:", result.Stderr);
    }

    // --- show ---

    [Fact]
    public async Task Show_WritesHtmlWithoutBomToStdout()
    {
        string file = WriteFile("ok.xml", TestData.ValidInvoice());

        CliResult result = await Ksef.RunAsync(dir.Path, "show", file);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
        Assert.False(result.StdoutBytes.AsSpan().StartsWith(TestData.Bom));
        Assert.StartsWith("<!DOCTYPE html", result.Stdout);
        Assert.Contains("1111111111", result.Stdout);
    }

    [Fact]
    public async Task Show_ReadsStdinAndWritesOutputFile()
    {
        string output = dir.File("f.html");

        CliResult result = await Ksef.RunAsync(dir.Path, TestData.ValidInvoice(), "show", "-o", output);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdoutBytes);
        Assert.Contains("e-FAKTURA KSeF", File.ReadAllText(output));
    }

    [Fact]
    public async Task Show_BrokenXml_FailsWithoutPartialOutput()
    {
        string output = dir.File("f.html");

        CliResult toStdout = await Ksef.RunAsync(dir.Path, "<a>"u8.ToArray(), "show");
        CliResult toFile = await Ksef.RunAsync(dir.Path, "<a>"u8.ToArray(), "show", "-o", output);

        Assert.Equal(1, toStdout.ExitCode);
        Assert.Empty(toStdout.StdoutBytes);
        Assert.StartsWith("ksef: <stdin>:", toStdout.Stderr);
        Assert.Equal(1, toFile.ExitCode);
        Assert.False(File.Exists(output));
    }

    // --- cert new ---

    [Fact]
    public async Task CertNew_WritesPersonalCertificateWithKeyToStdout()
    {
        CliResult result = await Ksef.RunAsync(dir.Path, "cert", "new", "--nip", "5908996669");

        Assert.Equal(0, result.ExitCode);
        using X509Certificate2 cert = X509Certificate2.CreateFromPem(result.Stdout, result.Stdout);
        Assert.True(cert.HasPrivateKey);
        Assert.Contains("SERIALNUMBER=TINPL-5908996669", cert.Subject);
        Assert.Contains("C=PL", cert.Subject);
        Assert.True(cert.NotAfter > DateTime.Now.AddYears(1));
    }

    [Fact]
    public async Task CertNew_Seal_UsesOrganizationIdentifier()
    {
        CliResult result = await Ksef.RunAsync(dir.Path, "cert", "new", "--seal", "--nip", "5908996669");

        Assert.Equal(0, result.ExitCode);
        using X509Certificate2 cert = X509Certificate2.CreateFromPem(result.Stdout, result.Stdout);
        Assert.Contains("organizationIdentifier=VATPL-5908996669", cert.Subject);
    }

    [Fact]
    public async Task CertNew_OutputFile_IsPrivateAndNeverOverwritten()
    {
        string pem = dir.File("cert.pem");

        CliResult first = await Ksef.RunAsync(dir.Path, "cert", "new", "--nip", "5908996669", "-o", pem);
        byte[] content = File.ReadAllBytes(pem);
        CliResult second = await Ksef.RunAsync(dir.Path, "cert", "new", "--nip", "5908996669", "-o", pem);

        Assert.Equal(0, first.ExitCode);
        Assert.Empty(first.StdoutBytes);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(pem));
        Assert.Equal(1, second.ExitCode);
        Assert.Contains("already exists", second.Stderr);
        Assert.Equal(content, File.ReadAllBytes(pem));
    }

    [Theory]
    [InlineData("1234567890")]
    [InlineData("abc")]
    public async Task CertNew_RejectsInvalidNip(string nip)
    {
        CliResult result = await Ksef.RunAsync(dir.Path, "cert", "new", "--nip", nip);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Niepoprawny NIP", result.Stderr);
        Assert.DoesNotContain("PRIVATE KEY", result.Stdout);
    }

    [Fact]
    public async Task CertNew_RefusesToPrintKeyOnTerminal()
    {
        // `script` daje procesowi pseudoterminal jako stdout.
        // Ksef.CommandLine: przy make test-aot sprawdza binarkę AOT, nie ksef.dll.
        CliResult result = await RunInPseudoTerminalAsync($"{Ksef.CommandLine} cert new --nip 5908996669");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("nie wypiszę klucza prywatnego na terminal", result.Stdout);
        Assert.DoesNotContain("PRIVATE KEY", result.Stdout);
    }

    // --- sesje bez sieci ---

    [Fact]
    public async Task Session_NoSessions_FailsQuietly()
    {
        CliResult text = await Ksef.RunAsync(dir.Path, "session");
        CliResult json = await Ksef.RunAsync(dir.Path, "session", "--json");

        Assert.Equal(1, text.ExitCode);
        Assert.Empty(text.StdoutBytes);
        Assert.Equal(1, json.ExitCode);
        Assert.Equal("[]", json.Stdout.Trim());
    }

    [Fact]
    public async Task Logout_NoSession_ExplainsHowToLogIn()
    {
        CliResult result = await Ksef.RunAsync(dir.Path, "logout");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("ksef login", result.Stderr);
    }

    [Fact]
    public async Task Login_GarbageCertificate_FailsBeforeContactingKsef()
    {
        CliResult result = await Ksef.RunAsync(dir.Path, "abc"u8.ToArray(), "login", "--nip", "5908996669", "--cert", "-");

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("ksef: <stdin>: nie udało się wczytać certyfikatu", result.Stderr);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "ksef")));
    }

    [Fact]
    public async Task Login_CertificateWithoutKey_Fails()
    {
        CliResult generated = await Ksef.RunAsync(dir.Path, "cert", "new", "--nip", "5908996669");
        string certOnly = generated.Stdout[..(generated.Stdout.IndexOf("-----END CERTIFICATE-----", StringComparison.Ordinal) + "-----END CERTIFICATE-----".Length)];

        CliResult result = await Ksef.RunAsync(dir.Path, Encoding.ASCII.GetBytes(certOnly), "login", "--nip", "5908996669", "--cert", "-");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("nie udało się wczytać certyfikatu z kluczem", result.Stderr);
    }

    [Fact]
    public async Task List_NoSession_ExplainsHowToLogIn()
    {
        CliResult result = await Ksef.RunAsync(dir.Path, "list");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdoutBytes);
        Assert.Contains("ksef login", result.Stderr);
    }

    [Theory]
    [InlineData("--from", "wczoraj")]
    [InlineData("--to", "2026-02-30")]
    public async Task List_InvalidDate_IsRejected(string option, string value)
    {
        CliResult result = await Ksef.RunAsync(dir.Path, "list", option, value);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"Niepoprawna data: {value}", result.Stderr);
    }

    [Fact]
    public async Task List_FromAfterTo_IsRejectedBeforeContactingKsef()
    {
        CliResult result = await Ksef.RunAsync(dir.Path, "list", "--from", "2026-11-01", "--to", "2026-10-01");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("późniejsza", result.Stderr);
    }

    [Theory]
    [InlineData("--subject", "nabywca")]
    [InlineData("--date-type", "wystawienia")]
    public async Task List_UnknownChoice_IsRejected(string option, string value)
    {
        CliResult result = await Ksef.RunAsync(dir.Path, "list", option, value);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(value, result.Stderr);
    }

    [Fact]
    public async Task Send_InvalidFile_SendsNothingAndNeedsNoSession()
    {
        // Brak sesji, a i tak błąd dotyczy pliku: kontrola lokalna jest przed kontaktem z KSeF.
        string ok = WriteFile("ok.xml", TestData.ValidInvoice());
        string bad = WriteFile("bom.xml", WithBom(TestData.ValidInvoice()));

        CliResult result = await Ksef.RunAsync(dir.Path, "send", ok, bad);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdoutBytes);
        Assert.StartsWith($"{bad}:1:1: KSeF błąd:", result.Stderr);
        Assert.Contains("nic nie wysłano", result.Stderr);
        Assert.DoesNotContain("ksef login", result.Stderr);
    }

    [Fact]
    public async Task Send_MissingFile_SendsNothing()
    {
        CliResult result = await Ksef.RunAsync(dir.Path, "send", dir.File("brak.xml"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("nic nie wysłano", result.Stderr);
    }

    [Fact]
    public async Task Send_ValidFileWithoutSession_ExplainsHowToLogIn()
    {
        CliResult result = await Ksef.RunAsync(dir.Path, TestData.ValidInvoice(), "send");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("ksef login", result.Stderr);
    }

    private const string KsefNumberA = "5908996669-20261030-7A9B0D000000-89";
    private const string KsefNumberB = "5908996669-20261030-7A9B1AC00000-98";

    [Fact]
    public async Task Get_NoNumbers_Fails()
    {
        CliResult result = await Ksef.RunAsync(dir.Path, ""u8.ToArray(), "get");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("brak numerów KSeF", result.Stderr);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("5908996669-20261030-7A9B0D000000")] // bez sumy kontrolnej
    [InlineData("1000000006-20261030-7A9B0D000000-89")] // NIP spoza wzorca KSeF
    public async Task Get_InvalidKsefNumber_IsRejectedBeforeContactingKsef(string number)
    {
        CliResult result = await Ksef.RunAsync(dir.Path, "get", number);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"niepoprawny numer KSeF: {number}", result.Stderr);
    }

    [Fact]
    public async Task Get_SeveralNumbersToStdout_RequiresDirectory()
    {
        CliResult result = await Ksef.RunAsync(dir.Path, "get", KsefNumberA, KsefNumberB);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("-d", result.Stderr);
    }

    [Fact]
    public async Task Get_AllAlreadyDownloaded_NeedsNeitherSessionNorNetwork()
    {
        // Wejście w formacie `ksef list` (TSV): numer KSeF w pierwszej kolumnie, powtórka i pusty wiersz.
        string target = Path.Combine(dir.Path, "faktury");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, KsefNumberA + ".xml"), "<a/>");
        File.WriteAllText(Path.Combine(target, KsefNumberB + ".xml"), "<b/>");
        byte[] tsv = Encoding.UTF8.GetBytes($"{KsefNumberB}\tFV/2\n\n{KsefNumberA}\tFV/1\n{KsefNumberB}\tFV/2\n");

        CliResult result = await Ksef.RunAsync(dir.Path, tsv, "get", "-d", target);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Stderr);
        Assert.Equal($"{target}/{KsefNumberB}.xml\n{target}/{KsefNumberA}.xml\n", result.Stdout);
        Assert.Equal("<a/>", File.ReadAllText(Path.Combine(target, KsefNumberA + ".xml")));
    }

    [Fact]
    public async Task Get_MissingFileWithoutSession_ExplainsHowToLogIn()
    {
        CliResult result = await Ksef.RunAsync(dir.Path, "get", "-d", Path.Combine(dir.Path, "nowy"), KsefNumberA);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("ksef login", result.Stderr);
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "nowy")));
    }

    // --- błędy środowiska: zawsze `ksef: …`, nigdy zrzut stosu ---

    private static void AssertCleanError(CliResult result)
    {
        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("ksef: ", result.Stderr);
        Assert.DoesNotContain("Unhandled exception", result.Stderr);
        Assert.DoesNotContain("   at ", result.Stderr);
    }

    [Fact]
    public async Task CorruptedSessionFile_IsReportedWithPath()
    {
        Directory.CreateDirectory(Path.Combine(dir.Path, "ksef"));
        string broken = Path.Combine(dir.Path, "ksef", "test-5908996669.json");
        File.WriteAllText(broken, "{ niedokończony");

        CliResult session = await Ksef.RunAsync(dir.Path, "session");
        CliResult list = await Ksef.RunAsync(dir.Path, "list");

        AssertCleanError(session);
        Assert.Contains(broken, session.Stderr);
        Assert.Contains("usuń go", session.Stderr);
        AssertCleanError(list);
    }

    [Fact]
    public async Task Get_DirectoryIsAFile_FailsCleanly()
    {
        string file = WriteFile("plik", [1]);

        AssertCleanError(await Ksef.RunAsync(dir.Path, "get", "-d", file, KsefNumberA));
    }

    [Fact]
    public async Task Upo_DirectoryIsAFile_FailsCleanly()
    {
        // Bez sesji błąd sesji wyprzedza katalog — tu ważne tylko, że nie ma zrzutu stosu.
        string file = WriteFile("plik", [1]);

        AssertCleanError(await Ksef.RunAsync(dir.Path, "upo", "-d", file, "--session", "20261030-SO-394363C000-F9191A9DA9-98", KsefNumberA));
    }

    [Fact]
    public async Task CertNew_UnwritableOutput_FailsCleanly()
    {
        AssertCleanError(await Ksef.RunAsync(dir.Path, "cert", "new", "--nip", "5908996669", "-o", "/proc/ksef-test.pem"));
        AssertCleanError(await Ksef.RunAsync(dir.Path, "cert", "new", "--nip", "5908996669", "-o", Path.Combine(dir.Path, "brak", "c.pem")));
    }

    [Fact]
    public async Task Show_UnwritableOutput_FailsCleanly()
    {
        AssertCleanError(await Ksef.RunAsync(dir.Path, TestData.ValidInvoice(), "show", "-o", "/proc/ksef-test.html"));
    }

    [Fact]
    public async Task Login_WithoutRuntimeDir_FailsBeforeContactingKsef()
    {
        // Pusty XDG_RUNTIME_DIR: błąd przed odczytem certyfikatu (garbage na stdin by go inaczej zgłosił) i przed siecią.
        CliResult result = await Ksef.RunAsync("", "abc"u8.ToArray(), "login", "--nip", "5908996669", "--cert", "-");

        AssertCleanError(result);
        Assert.Contains("XDG_RUNTIME_DIR", result.Stderr);
    }

    [Fact]
    public async Task UnknownEnvironment_IsRejected()
    {
        CliResult result = await Ksef.RunAsync(dir.Path, "session", "-e", "prd");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("prd", result.Stderr);
    }

    private static async Task<CliResult> RunInPseudoTerminalAsync(string command)
    {
        System.Diagnostics.ProcessStartInfo start = new("script")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in new[] { "-qec", command, "/dev/null" })
        {
            start.ArgumentList.Add(arg);
        }
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new CliResult(process.ExitCode, Encoding.UTF8.GetBytes(stdout), stderr);
    }
}
