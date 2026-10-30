using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

namespace KsefCli.Tests;

/// <summary>
/// Token PKCS#11 na czas testu: SoftHSM z własnym katalogiem tokenów (`SOFTHSM2_CONF`) i kluczem z `ksef cert new`.
/// `ksef` widzi go przez p11-kit, jak prawdziwą kartę. Bez softhsm2 albo engine'u pkcs11 (libp11) testy się pomijają.
/// </summary>
internal sealed class SoftHsmToken : IDisposable
{
    public const string Pin = "735921";
    public const string KeyUri = "pkcs11:object=ksef-test;type=private";

    private readonly TempDir dir = new();

    private SoftHsmToken(string nip)
    {
        Nip = nip;
        Label = "ksef-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(dir.File("tokens"));
        File.WriteAllText(dir.File("softhsm2.conf"), $"directories.tokendir = {dir.File("tokens")}\nobjectstore.backend = file\n");
        Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["SOFTHSM2_CONF"] = dir.File("softhsm2.conf") };
        CertificateFile = dir.File("cert.pem");
        PinFile = dir.File("pin");
        File.WriteAllText(PinFile, Pin + "\n");
    }

    public string Nip { get; }

    public string Label { get; }

    public IReadOnlyDictionary<string, string> Environment { get; }

    /// <summary>Sam certyfikat (bez klucza) — klucz jest tylko na tokenie.</summary>
    public string CertificateFile { get; }

    public string PinFile { get; }

    public string Uri => $"pkcs11:token={Label};object=ksef-test;type=private";

    /// <summary>Powód pominięcia testów albo null, gdy softhsm2 i engine pkcs11 są dostępne.</summary>
    public static string? Unavailable { get; } = FindUnavailable();

    public static async Task<SoftHsmToken> CreateAsync(string nip)
    {
        SoftHsmToken token = new(nip);
        string full = token.dir.File("cert-with-key.pem");
        CliResult cert = await Ksef.RunAsync(token.dir.Path, "cert", "new", "--nip", nip, "-o", full);
        Assert.True(cert.ExitCode == 0, cert.Stderr);
        string pem = await File.ReadAllTextAsync(full, TestContext.Current.CancellationToken);
        using X509Certificate2 certificate = X509Certificate2.CreateFromPem(pem, pem);
        await File.WriteAllTextAsync(token.CertificateFile, certificate.ExportCertificatePem(), TestContext.Current.CancellationToken);
        using RSA key = certificate.GetRSAPrivateKey()!;
        string keyFile = token.dir.File("key.pem");
        await File.WriteAllTextAsync(keyFile, key.ExportPkcs8PrivateKeyPem(), TestContext.Current.CancellationToken);

        token.SoftHsm("--init-token", "--free", "--label", token.Label, "--pin", Pin, "--so-pin", "5678");
        token.SoftHsm("--import", keyFile, "--token", token.Label, "--label", "ksef-test", "--id", "01", "--pin", Pin);
        File.Delete(keyFile);
        return token;
    }

    public void Dispose() => dir.Dispose();

    private void SoftHsm(params string[] args)
    {
        (int exit, string output) = Run("softhsm2-util", args, Environment);
        Assert.True(exit == 0, output);
    }

    private static string? FindUnavailable()
    {
        try
        {
            if (Run("softhsm2-util", ["--version"], null).Exit != 0)
            {
                return "brak softhsm2-util";
            }
            return Run("openssl", ["engine", "-t", "pkcs11"], null).Exit == 0 ? null : "brak engine'u pkcs11 OpenSSL (libengine-pkcs11-openssl)";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "brak softhsm2-util albo openssl";
        }
    }

    private static (int Exit, string Output) Run(string file, string[] args, IReadOnlyDictionary<string, string>? environment)
    {
        ProcessStartInfo start = new(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        foreach ((string name, string value) in environment ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            start.Environment[name] = value;
        }
        using Process process = Process.Start(start)!;
        process.StandardInput.Close();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr.Result);
    }
}

/// <summary>`ksef login --cert cert.pem --key pkcs11:…` przeciw <see cref="FakeKsef"/>: klucz nie opuszcza tokenu.</summary>
public class LoginPkcs11Tests
{
    private const string Nip = "5908996669";

    public LoginPkcs11Tests() => Assert.SkipWhen(SoftHsmToken.Unavailable is not null, $"PKCS#11: {SoftHsmToken.Unavailable}");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Login_KeyOnToken_SignsRequest_SilentOnSuccess(bool pinFromStdin)
    {
        using SoftHsmToken token = await SoftHsmToken.CreateAsync(Nip);
        using FakeKsef ksef = LoginExternalTests.AuthenticatingKsef();
        using TempDir dir = new();

        CliResult login = pinFromStdin
            ? await ksef.RunAsync(dir.Path, System.Text.Encoding.UTF8.GetBytes(SoftHsmToken.Pin + "\n"), token.Environment,
                "login", "--nip", Nip, "--cert", token.CertificateFile, "--key", token.Uri, "--pin-file", "-", "--key-via", "engine")
            : await ksef.RunAsync(dir.Path, null, token.Environment,
                "login", "--nip", Nip, "--cert", token.CertificateFile, "--key", token.Uri, "--pin-file", token.PinFile, "--key-via", "engine");

        Assert.True(login.ExitCode == 0, login.Stderr);
        Assert.Equal("", login.Stderr);
        Assert.Empty(login.StdoutBytes);

        // Podpis złożony kluczem z tokenu weryfikuje się certyfikatem.
        XmlDocument xml = new() { PreserveWhitespace = true };
        xml.LoadXml(Assert.Single(ksef.RequestsTo("POST", "auth/xades-signature")).Body);
        SignedXml signed = new(xml);
        signed.LoadXml((XmlElement)xml.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0]!);
        using X509Certificate2 certificate = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(token.CertificateFile, TestContext.Current.CancellationToken));
        Assert.True(signed.CheckSignature(certificate, verifySignatureOnly: true), "podpis nie weryfikuje się certyfikatem");
        Assert.Equal(0, (await ksef.RunAsync(dir.Path, "session")).ExitCode);
    }

    [Fact]
    public async Task Login_WrongPin_ExplainsWithoutRevealingPin()
    {
        using SoftHsmToken token = await SoftHsmToken.CreateAsync(Nip);
        using FakeKsef ksef = LoginExternalTests.AuthenticatingKsef();
        using TempDir dir = new();
        string pinFile = dir.File("pin");
        await File.WriteAllTextAsync(pinFile, "480613", TestContext.Current.CancellationToken);

        CliResult login = await ksef.RunAsync(dir.Path, null, token.Environment,
            "login", "--nip", Nip, "--cert", token.CertificateFile, "--key", token.Uri, "--pin-file", pinFile, "--key-via", "engine");

        Assert.Equal(1, login.ExitCode);
        Assert.StartsWith($"ksef: {token.Uri}: nie udało się użyć klucza", login.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("480613", login.Stderr, StringComparison.Ordinal);
        Assert.Empty(ksef.RequestsTo("POST", "auth/challenge"));
    }

    [Fact]
    public async Task Login_KeyDoesNotMatchCertificate_IsRejected()
    {
        using SoftHsmToken token = await SoftHsmToken.CreateAsync(Nip);
        using FakeKsef ksef = LoginExternalTests.AuthenticatingKsef();
        using TempDir dir = new();
        string other = dir.File("other.pem");
        Assert.Equal(0, (await Ksef.RunAsync(dir.Path, "cert", "new", "--nip", Nip, "-o", other)).ExitCode);

        CliResult login = await ksef.RunAsync(dir.Path, null, token.Environment,
            "login", "--nip", Nip, "--cert", other, "--key", token.Uri, "--pin-file", token.PinFile, "--key-via", "engine");

        Assert.Equal(1, login.ExitCode);
        Assert.Contains("nie udało się użyć klucza", login.Stderr, StringComparison.Ordinal);
        Assert.Empty(ksef.RequestsTo("POST", "auth/xades-signature"));
    }
}

/// <summary>Kontrola opcji klucza na karcie — bez tokenu i bez sieci.</summary>
public class LoginKeyOptionTests
{
    [Theory]
    [InlineData("--key wymaga --cert", "--token-file", "t", "--key", SoftHsmToken.KeyUri)]
    [InlineData("--pin-file i --key-via dotyczą tylko klucza na karcie", "--cert", "c.pem", "--pin-file", "p")]
    [InlineData("--pin-file i --key-via dotyczą tylko klucza na karcie", "--cert", "c.pem", "--key-via", "engine")]
    [InlineData("Na stdin może iść tylko jedno", "--cert", "-", "--key", SoftHsmToken.KeyUri, "--pin-file", "-")]
    public async Task Login_KeyOptions_AreValidated(string expected, params string[] options)
    {
        using TempDir dir = new();

        CliResult result = await Ksef.RunAsync(dir.Path, ["login", "--nip", "5908996669", .. options]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expected, result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("engine", "OpenSSL nie ma engine'u pkcs11", "--key-via provider")]
    [InlineData("provider", "OpenSSL nie ma providera pkcs11", "--key-via engine")]
    public async Task Login_MissingOpenSslModule_NamesPackageAndAlternative(string via, string expected, string alternative)
    {
        using TempDir dir = new();
        string cert = dir.File("cert.pem");
        string pin = dir.File("pin");
        await File.WriteAllTextAsync(pin, "1234", TestContext.Current.CancellationToken);
        Assert.Equal(0, (await Ksef.RunAsync(dir.Path, "cert", "new", "--nip", "5908996669", "-o", cert)).ExitCode);
        string empty = dir.File("brak-modulow");
        Directory.CreateDirectory(empty);
        Dictionary<string, string> environment = new(StringComparer.Ordinal) { ["OPENSSL_ENGINES"] = empty, ["OPENSSL_MODULES"] = empty };

        CliResult result = await Ksef.RunAsync(dir.Path, null, environment,
            "login", "--nip", "5908996669", "--cert", cert, "--key", SoftHsmToken.KeyUri, "--pin-file", pin, "--key-via", via);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(expected, result.Stderr, StringComparison.Ordinal);
        Assert.Contains(alternative, result.Stderr, StringComparison.Ordinal);
    }
}
