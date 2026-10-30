using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using KSeF.Client.Api.Services;
using KsefCli.Commands;

namespace KsefCli.Tests;

/// <summary>
/// `ksef login --request` / `--signed` przeciw <see cref="FakeKsef"/>: żądanie podpisuje „inny program”
/// (tu podpis XAdES biblioteki MF w procesie testu, jak zrobiłby to Profil Zaufany czy aplikacja do podpisu).
/// </summary>
public class LoginExternalTests
{
    private const string Nip = "5908996669";
    private const string Reference = "20261030-AU-1234567890-ABCDEF0123-47";
    private const string Challenge = "20261030-CR-0123456789-ABCDEF0123-04";

    /// <summary>KSeF przyjmujący logowanie podpisem XAdES (dowolnym — weryfikację podpisu robią testy).</summary>
    internal static FakeKsef AuthenticatingKsef()
    {
        FakeKsef ksef = new();
        DateTimeOffset until = DateTimeOffset.UtcNow.AddMinutes(15);
        ksef.On("POST", "auth/challenge", (_, _) => FakeResponse.Json(new
        {
            challenge = Challenge,
            timestamp = DateTimeOffset.UtcNow,
            timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            clientIp = "127.0.0.1",
        }));
        ksef.On("POST", "auth/xades-signature", (_, _) => FakeResponse.Json(new
        {
            referenceNumber = Reference,
            authenticationToken = new { token = "fake-authentication-token", validUntil = until },
        }));
        ksef.On("GET", $"auth/{Reference}", (_, _) => FakeResponse.Json(new
        {
            startDate = DateTimeOffset.UtcNow,
            status = new { code = 200, description = "Uwierzytelnianie zakończone sukcesem" },
        }));
        ksef.On("POST", "auth/token/redeem", (_, _) => FakeResponse.Json(new
        {
            accessToken = new { token = FakeKsef.AccessToken, validUntil = until },
            refreshToken = new { token = "fake-refresh-token", validUntil = until.AddDays(7) },
        }));
        return ksef;
    }

    /// <summary>„Inny program do podpisu”: podpis XAdES certyfikatem z `ksef cert new`.</summary>
    internal static async Task<string> SignExternallyAsync(TempDir dir, string nip, string unsignedXml)
    {
        string pem = dir.File($"cert-{nip}.pem");
        if (!File.Exists(pem))
        {
            CliResult cert = await Ksef.RunAsync(dir.Path, "cert", "new", "--nip", nip, "-o", pem);
            Assert.True(cert.ExitCode == 0, cert.Stderr);
        }
        string text = await File.ReadAllTextAsync(pem, TestContext.Current.CancellationToken);
        using X509Certificate2 certificate = X509Certificate2.CreateFromPem(text, text);
        return SignatureService.Sign(unsignedXml, certificate);
    }

    [Fact]
    public async Task Request_PrintsUnsignedRequestWithChallenge_SavesNothing()
    {
        using FakeKsef ksef = AuthenticatingKsef();
        using TempDir dir = new();

        CliResult result = await ksef.RunAsync(dir.Path, "login", "--nip", Nip, "--request");

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.False(result.StdoutBytes.AsSpan().StartsWith(TestData.Bom));
        XmlDocument xml = new();
        xml.LoadXml(result.Stdout);
        Assert.Equal("AuthTokenRequest", xml.DocumentElement!.LocalName);
        Assert.Equal(Challenge, xml.GetElementsByTagName("Challenge")[0]!.InnerText);
        Assert.Equal(Nip, xml.GetElementsByTagName("Nip")[0]!.InnerText);
        Assert.Empty(xml.GetElementsByTagName("Signature", "http://www.w3.org/2000/09/xmldsig#"));
        Assert.Empty(ksef.RequestsTo("POST", "auth/xades-signature"));
        Assert.Equal(1, (await ksef.RunAsync(dir.Path, "session")).ExitCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Signed_SendsExternallySignedRequestUnchanged_SavesSession(bool fromStdin, bool withBom)
    {
        using FakeKsef ksef = AuthenticatingKsef();
        using TempDir dir = new();
        CliResult request = await ksef.RunAsync(dir.Path, "login", "--nip", Nip, "--request");
        Assert.Equal(0, request.ExitCode);
        string signed = await SignExternallyAsync(dir, Nip, request.Stdout);
        byte[] bytes = [.. withBom ? TestData.Bom : [], .. Encoding.UTF8.GetBytes(signed)];
        string file = dir.File("signed.xml");
        await File.WriteAllBytesAsync(file, bytes, TestContext.Current.CancellationToken);

        CliResult login = fromStdin
            ? await ksef.RunAsync(dir.Path, bytes, "login", "--nip", Nip, "--signed", "-")
            : await ksef.RunAsync(dir.Path, "login", "--nip", Nip, "--signed", file);

        Assert.True(login.ExitCode == 0, login.Stderr);
        Assert.Empty(login.StdoutBytes);
        // Podpis obejmuje treść — KSeF musi dostać dokładnie to, co podpisano.
        Assert.Equal(signed, Assert.Single(ksef.RequestsTo("POST", "auth/xades-signature")).Body);
        Assert.Equal(0, (await ksef.RunAsync(dir.Path, "session")).ExitCode);
    }

    [Fact]
    public async Task Signed_RejectsLocalMistakesBeforeNetwork()
    {
        using FakeKsef ksef = AuthenticatingKsef();
        using TempDir dir = new();
        string unsigned = (await ksef.RunAsync(dir.Path, "login", "--nip", Nip, "--request")).Stdout;
        string file = dir.File("request.xml");
        await File.WriteAllTextAsync(file, unsigned, TestContext.Current.CancellationToken);

        CliResult notSigned = await ksef.RunAsync(dir.Path, "login", "--nip", Nip, "--signed", file);
        CliResult otherNip = await ksef.RunAsync(dir.Path, "login", "--nip", "1111111111", "--signed", file);

        Assert.Equal(1, notSigned.ExitCode);
        Assert.Contains("nie jest podpisane", notSigned.Stderr);
        Assert.Equal(1, otherNip.ExitCode);
        Assert.Contains($"żądanie jest dla NIP {Nip}, a logujesz się na 1111111111", otherNip.Stderr);
        Assert.Empty(ksef.RequestsTo("POST", "auth/xades-signature"));
    }

    [Theory]
    [InlineData("nie XML", "to nie jest poprawny XML")]
    [InlineData("<Faktura/>", "to nie jest żądanie logowania KSeF")]
    [InlineData("<!DOCTYPE a [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><AuthTokenRequest>&x;</AuthTokenRequest>", "to nie jest poprawny XML")]
    [InlineData("<AuthTokenRequest><ContextIdentifier><Nip>5908996669</Nip></ContextIdentifier></AuthTokenRequest>", "nie jest podpisane")]
    public void CheckSignedRequest_ExplainsProblem(string xml, string expected) =>
        Assert.Contains(expected, LoginCommand.CheckSignedRequest(xml, Nip), StringComparison.Ordinal);
}
