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
        string dll = Path.Combine(AppContext.BaseDirectory, "ksef.dll");
        CliResult result = await RunInPseudoTerminalAsync($"dotnet {dll} cert new --nip 5908996669");

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
        string certOnly = generated.Stdout[..(generated.Stdout.IndexOf("-----END CERTIFICATE-----") + "-----END CERTIFICATE-----".Length)];

        CliResult result = await Ksef.RunAsync(dir.Path, Encoding.ASCII.GetBytes(certOnly), "login", "--nip", "5908996669", "--cert", "-");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("nie udało się wczytać certyfikatu z kluczem", result.Stderr);
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
