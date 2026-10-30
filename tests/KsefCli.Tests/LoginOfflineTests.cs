using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

namespace KsefCli.Tests;

/// <summary>
/// `ksef login` bez sieci, przeciw <see cref="FakeKsef"/>: pełny przebieg (wyzwanie, podpis XAdES, status, tokeny)
/// i podpis sprawdzony po stronie „serwera”. Na binarce Native AOT (make test-aot) to jedyny test bez sieci
/// dla XmlSerializer żądania logowania (ILLink.Descriptors.xml) i algorytmów podpisu (XmlDsigAlgorithms).
/// </summary>
public class LoginOfflineTests
{
    private const string Nip = "5908996669";
    private const string Reference = "20261030-AU-1234567890-ABCDEF0123-45";
    private const string Challenge = "20261030-CR-0123456789-ABCDEF0123-01";

    /// <summary>KSeF przyjmujący logowanie; `status` decyduje o wyniku weryfikacji podpisu (domyślnie 200).</summary>
    private static FakeKsef AuthenticatingKsef(object? status = null)
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
            authenticationMethod = "QualifiedSeal",
            status = status ?? new { code = 200, description = "Uwierzytelnianie zakończone sukcesem" },
        }));
        ksef.On("POST", "auth/token/redeem", (_, _) => FakeResponse.Json(new
        {
            accessToken = new { token = FakeKsef.AccessToken, validUntil = until },
            refreshToken = new { token = "fake-refresh-token", validUntil = DateTimeOffset.UtcNow.AddDays(7) },
        }));
        return ksef;
    }

    private static async Task<string> NewCertificateAsync(TempDir dir)
    {
        string pem = dir.File("cert.pem");
        CliResult result = await Ksef.RunAsync(dir.Path, "cert", "new", "--seal", "--nip", Nip, "-o", pem);
        Assert.True(result.ExitCode == 0, result.Stderr);
        return pem;
    }

    [Fact]
    public async Task Login_SendsSignedChallenge_AndSavesSession()
    {
        using TempDir dir = new();
        using FakeKsef ksef = AuthenticatingKsef();
        string pem = await NewCertificateAsync(dir);

        CliResult login = await ksef.RunAsync(dir.Path, "login", "--nip", Nip, "--cert", pem);

        Assert.True(login.ExitCode == 0, login.Stderr);
        Assert.Empty(login.StdoutBytes);

        FakeRequest submission = Assert.Single(ksef.RequestsTo("POST", "auth/xades-signature"));
        XmlDocument xml = new() { PreserveWhitespace = true };
        xml.LoadXml(submission.Body);
        Assert.Equal(Challenge, Single(xml, "Challenge").InnerText);
        Assert.Equal(Nip, Single(xml, "Nip").InnerText);

        SignedXml signed = new(xml);
        signed.LoadXml((XmlElement)xml.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0]!);
        string pemText = await File.ReadAllTextAsync(pem, TestContext.Current.CancellationToken);
        using X509Certificate2 certificate = X509Certificate2.CreateFromPem(pemText);
        Assert.True(signed.CheckSignature(certificate, verifySignatureOnly: true), "podpis XAdES nie weryfikuje się kluczem certyfikatu");
        Assert.Equal(SignedXml.XmlDsigRSASHA256Url, signed.SignatureMethod);

        // Token z wyzwania wymieniony na tokeny sesji, a sesja zapisana pod NIP-em.
        Assert.Equal("Bearer fake-authentication-token", Assert.Single(ksef.RequestsTo("POST", "auth/token/redeem")).Authorization);
        CliResult session = await ksef.RunAsync(dir.Path, "session", "--json");
        Assert.True(session.ExitCode == 0, session.Stderr);
        Assert.Contains(Nip, session.Stdout);
    }

    [Fact]
    public async Task Login_RejectedByKsef_ShowsReason_AndSavesNoSession()
    {
        using TempDir dir = new();
        using FakeKsef ksef = AuthenticatingKsef(new { code = 415, description = "Uwierzytelnienie zakończone niepowodzeniem", details = new[] { "Brak uprawnień" } });
        string pem = await NewCertificateAsync(dir);

        CliResult login = await ksef.RunAsync(dir.Path, "login", "--nip", Nip, "--cert", pem);

        Assert.Equal(1, login.ExitCode);
        Assert.Contains("415", login.Stderr);
        Assert.Contains("Brak uprawnień", login.Stderr);
        Assert.Empty(ksef.RequestsTo("POST", "auth/token/redeem"));
        Assert.Equal(1, (await ksef.RunAsync(dir.Path, "session")).ExitCode);
    }

    private static XmlElement Single(XmlDocument xml, string localName) =>
        Assert.Single(xml.SelectNodes($"//*[local-name()='{localName}']")!.Cast<XmlElement>());
}
