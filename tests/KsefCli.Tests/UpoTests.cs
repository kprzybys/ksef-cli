using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using KsefCli.Commands;

namespace KsefCli.Tests;

public class UpoParseTests
{
    private const string KsefA = "5908996669-20261030-7C4E9AC00000-D0";
    private const string KsefB = "5908996669-20261030-7C4EA1C00000-FA";
    private const string SessionX = "20261030-SO-394363C000-F9191A9DA9-98";
    private const string SessionY = "20261030-SO-0000000000-0000000000-00";

    [Fact]
    public void SendOutput_TakesSessionFromThirdColumn()
    {
        IReadOnlyList<UpoCommand.UpoRequest> requests = UpoCommand.ParseRequests([], null,
            [$"{KsefA}\tfaktury/a.xml\t{SessionX}", "", $"{KsefB}\tfaktury/b.xml\t{SessionY}"]);

        Assert.Equal([new(KsefA, SessionX), new(KsefB, SessionY)], requests);
    }

    [Fact]
    public void ListOutput_UsesSessionOption()
    {
        // W `ksef list` trzecia kolumna to data wystawienia — nie może zostać wzięta za numer sesji.
        IReadOnlyList<UpoCommand.UpoRequest> requests = UpoCommand.ParseRequests([], SessionX,
            [KsefA, $"{KsefB}\tFV/2\t2026-10-30\t5908996669\tFirma"]);

        Assert.Equal([new(KsefA, SessionX), new(KsefB, SessionX)], requests);
    }

    [Fact]
    public void Arguments_UseSessionOption_AndDeduplicate()
    {
        IReadOnlyList<UpoCommand.UpoRequest> requests = UpoCommand.ParseRequests([KsefA, KsefB, KsefA], SessionX, []);

        Assert.Equal([new(KsefA, SessionX), new(KsefB, SessionX)], requests);
    }

    [Fact]
    public void MissingSession_IsAnError()
    {
        FormatException ex = Assert.Throws<FormatException>(() => UpoCommand.ParseRequests([KsefA], null, []));

        Assert.Contains("--session", ex.Message);
    }

    [Fact]
    public void InvalidKsefNumber_IsAnError() =>
        Assert.Contains("niepoprawny numer KSeF: abc", Assert.Throws<FormatException>(() => UpoCommand.ParseRequests(["abc"], SessionX, [])).Message);

    [Fact]
    public void NoInput_IsAnError() =>
        Assert.Contains("brak numerów", Assert.Throws<FormatException>(() => UpoCommand.ParseRequests([], SessionX, ["", "  "])).Message);
}

public class UpoCliOfflineTests : IDisposable
{
    private const string KsefA = "5908996669-20261030-7C4E9AC00000-D0";
    private const string KsefB = "5908996669-20261030-7C4EA1C00000-FA";
    private const string Session = "20261030-SO-394363C000-F9191A9DA9-98";

    private readonly TempDir dir = new();

    public void Dispose() => dir.Dispose();

    [Fact]
    public async Task Collective_RequiresSessionAndNoNumbers()
    {
        CliResult noSession = await Ksef.RunAsync(dir.Path, "upo", "--collective");
        CliResult withNumber = await Ksef.RunAsync(dir.Path, "upo", "--collective", "--session", Session, KsefA);

        Assert.Equal(1, noSession.ExitCode);
        Assert.Equal(1, withNumber.ExitCode);
        Assert.Contains("--collective wymaga --session", noSession.Stderr);
    }

    [Fact]
    public async Task InvalidSessionReference_IsRejectedBeforeContactingKsef()
    {
        CliResult result = await Ksef.RunAsync(dir.Path, "upo", "--session", "sesja-1", KsefA);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("niepoprawny numer sesji: sesja-1", result.Stderr);
    }

    [Fact]
    public async Task SeveralToStdout_RequiresDirectory()
    {
        CliResult result = await Ksef.RunAsync(dir.Path, "upo", "--session", Session, KsefA, KsefB);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("-d", result.Stderr);
    }

    [Fact]
    public async Task WithoutLogin_ExplainsHowToLogIn()
    {
        CliResult result = await Ksef.RunAsync(dir.Path, Encoding.UTF8.GetBytes($"{KsefA}\ta.xml\t{Session}\n"), "upo");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("ksef login", result.Stderr);
    }
}

/// <summary>UPO na żywym KSeF TEST — jak pozostałe testy E2E, tylko z <c>KSEF_E2E=1</c>.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public class UpoTestEnvironmentTests : IDisposable
{
    private readonly RuntimeDirScope runtime;

    public UpoTestEnvironmentTests()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KSEF_E2E") == "1", "Testy na KSeF TEST: ustaw KSEF_E2E=1.");
        runtime = new RuntimeDirScope();
    }

    public void Dispose() => runtime?.Dispose();

    private Task<CliResult> Ksef(params string[] args) => Tests.Ksef.RunAsync(runtime.Path, args);

    private async Task<string> LoginAsync()
    {
        string nip = TestData.RandomNip();
        string pem = Path.Combine(runtime.Path, $"cert-{nip}.pem");
        Assert.Equal(0, (await Ksef("cert", "new", "--nip", nip, "-o", pem)).ExitCode);
        CliResult login = await Ksef("login", "--nip", nip, "--cert", pem);
        Assert.True(login.ExitCode == 0, login.Stderr);
        return nip;
    }

    /// <summary>Wysyła faktury i zwraca wynik `ksef send` (numer KSeF, plik, sesja).</summary>
    private async Task<CliResult> SendAsync(string nip, int count)
    {
        string[] files = [.. Enumerable.Range(0, count).Select(i =>
        {
            string path = Path.Combine(runtime.Path, $"f{i}.xml");
            File.WriteAllBytes(path, TestData.InvoiceToSend(nip));
            return path;
        })];
        CliResult sent = await Ksef(["send", .. files]);
        Assert.True(sent.ExitCode == 0, sent.Stderr);
        return sent;
    }

    private static string[][] Rows(CliResult result) =>
        [.. result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t'))];

    private static XmlDocument Load(byte[] xml)
    {
        XmlDocument document = new() { PreserveWhitespace = true };
        document.Load(new MemoryStream(xml));
        return document;
    }

    /// <summary>Podpis MF pod UPO jest ważny — dowód, że dokument nie zmienił się w drodze (także przy przejściu przez string).</summary>
    private static void AssertSignatureValid(XmlDocument upo)
    {
        XmlElement signature = (XmlElement)upo.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0]!;
        SignedXml signed = new(upo);
        signed.LoadXml(signature);
        Assert.True(signed.CheckSignature(), "Podpis UPO nie przechodzi weryfikacji.");
    }

    private static string[] KsefNumbersIn(XmlDocument upo) =>
        [.. upo.GetElementsByTagName("NumerKSeFDokumentu").Cast<XmlNode>().Select(n => n.InnerText)];

    [Fact]
    public async Task Upo_FromSendOutput_IntoDirectory()
    {
        string nip = await LoginAsync();
        CliResult sent = await SendAsync(nip, 2);
        string target = Path.Combine(runtime.Path, "upo");

        CliResult result = await Tests.Ksef.RunAsync(runtime.Path, sent.StdoutBytes, "upo", "-d", target);

        Assert.True(result.ExitCode == 0, result.Stderr);
        string[] files = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(Rows(sent).Select(r => Path.Combine(target, r[0] + ".upo.xml")), files);
        foreach ((string file, string[] row) in files.Zip(Rows(sent)))
        {
            XmlDocument upo = Load(File.ReadAllBytes(file));
            Assert.Equal([row[0]], KsefNumbersIn(upo));
            Assert.Equal(row[2], upo.GetElementsByTagName("NumerReferencyjnySesji")[0]!.InnerText);
            AssertSignatureValid(upo);
        }
        Assert.Empty(Directory.GetFiles(target, "*.tmp"));
    }

    [Fact]
    public async Task Upo_SingleInvoiceToStdout()
    {
        string nip = await LoginAsync();
        string[] row = Rows(await SendAsync(nip, 1))[0];

        CliResult result = await Ksef("upo", "--session", row[2], row[0]);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.False(result.StdoutBytes.AsSpan().StartsWith(TestData.Bom));
        XmlDocument upo = Load(result.StdoutBytes);
        Assert.Equal("http://upo.schematy.mf.gov.pl/KSeF/v4-3", upo.DocumentElement!.NamespaceURI);
        Assert.Equal([row[0]], KsefNumbersIn(upo));
        AssertSignatureValid(upo);
    }

    [Fact]
    public async Task Upo_CollectiveCoversWholeSession()
    {
        string nip = await LoginAsync();
        string[][] rows = Rows(await SendAsync(nip, 2));

        CliResult result = await Ksef("upo", "--session", rows[0][2], "--collective");

        Assert.True(result.ExitCode == 0, result.Stderr);
        XmlDocument upo = Load(result.StdoutBytes);
        Assert.Equal(rows.Select(r => r[0]).Order(), KsefNumbersIn(upo).Where(n => n.Length > 0).Order());
        AssertSignatureValid(upo);
    }

    [Fact]
    public async Task Upo_UnknownSession_Fails()
    {
        string nip = await LoginAsync();
        string[] row = Rows(await SendAsync(nip, 1))[0];

        CliResult result = await Ksef("upo", "--session", "20261030-SO-0000000000-0000000000-00", row[0]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("21173", result.Stderr);
        Assert.Empty(result.StdoutBytes);
    }

    [Fact]
    public async Task Upo_InvoiceFromAnotherSession_Fails()
    {
        string nip = await LoginAsync();
        string[] first = Rows(await SendAsync(nip, 1))[0];
        string[] second = Rows(await SendAsync(nip, 1))[0];

        CliResult result = await Ksef("upo", "--session", first[2], second[0]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(second[0], result.Stderr);
    }
}

public class GetRetryTests
{
    private static KSeF.Client.Core.Exceptions.KsefApiException Error(int code) => new("błąd", System.Net.HttpStatusCode.BadRequest,
        new KSeF.Client.Core.Exceptions.ApiErrorResponse
        {
            Exception = new KSeF.Client.Core.Exceptions.ApiExceptionContent { ExceptionDetailList = [new(code, "opis")] },
        });

    [Fact]
    public void NotYetAvailable_IsRetried() => Assert.True(GetCommand.IsNotYetAvailable(Error(21165)));

    [Theory]
    [InlineData(21164)] // faktura nie istnieje — ponawianie nic nie da
    [InlineData(21405)]
    public void OtherErrors_AreNotRetried(int code) => Assert.False(GetCommand.IsNotYetAvailable(Error(code)));

    [Fact]
    public void ErrorWithoutBody_IsNotRetried() =>
        Assert.False(GetCommand.IsNotYetAvailable(new KSeF.Client.Core.Exceptions.KsefApiException("błąd", System.Net.HttpStatusCode.InternalServerError)));
}
