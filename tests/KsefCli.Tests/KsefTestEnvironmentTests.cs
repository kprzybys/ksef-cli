using System.Text.Json;
using KSeF.Client.Core.Exceptions;
using KSeF.Client.Core.Interfaces.Clients;
using Microsoft.Extensions.DependencyInjection;

namespace KsefCli.Tests;

/// <summary>
/// Testy na żywym środowisku KSeF TEST (sieć, certyfikaty podpisane samym sobą, losowy NIP na test).
/// Uruchamiane tylko na życzenie: <c>KSEF_E2E=1 dotnet test --project tests/KsefCli.Tests</c>.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public class KsefTestEnvironmentTests : IDisposable
{
    private readonly RuntimeDirScope runtime;

    public KsefTestEnvironmentTests()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("KSEF_E2E") == "1", "Testy na KSeF TEST: ustaw KSEF_E2E=1.");
        runtime = new RuntimeDirScope();
    }

    public void Dispose() => runtime?.Dispose();

    private Task<CliResult> Ksef(params string[] args) => Tests.Ksef.RunAsync(runtime.Path, args);

    private async Task<string> NewCertificateAsync(string nip)
    {
        string pem = Path.Combine(runtime.Path, $"cert-{nip}.pem");
        CliResult result = await Ksef("cert", "new", "--nip", nip, "-o", pem);
        Assert.Equal(0, result.ExitCode);
        return pem;
    }

    private async Task LoginAsync(string nip)
    {
        CliResult result = await Ksef("login", "--nip", nip, "--cert", await NewCertificateAsync(nip));
        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Empty(result.StdoutBytes);
    }

    [Fact]
    public async Task Status_ReachesTestApi()
    {
        CliResult result = await Ksef("status", "--json");

        Assert.True(result.ExitCode is 0 or 2, result.Stderr);
        using JsonDocument json = JsonDocument.Parse(result.Stdout);
        JsonElement api = json.RootElement.GetProperty("api");
        Assert.Equal("test", api.GetProperty("environment").GetString());
        Assert.True(api.GetProperty("reachable").GetBoolean());
        Assert.NotEmpty(api.GetProperty("certificates").EnumerateArray());
    }

    [Fact]
    public async Task Login_Session_Logout_FullCycle()
    {
        string nip = TestData.RandomNip();

        await LoginAsync(nip);

        CliResult list = await Ksef("session", "--json");
        Assert.Equal(0, list.ExitCode);
        using (JsonDocument json = JsonDocument.Parse(list.Stdout))
        {
            JsonElement session = Assert.Single(json.RootElement.EnumerateArray());
            Assert.Equal(nip, session.GetProperty("nip").GetString());
            Assert.Equal("test", session.GetProperty("environment").GetString());
            DateTimeOffset access = session.GetProperty("accessValidUntil").GetDateTimeOffset();
            DateTimeOffset refresh = session.GetProperty("refreshValidUntil").GetDateTimeOffset();
            Assert.InRange(access, DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddHours(2));
            Assert.InRange(refresh, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(30));
        }
        Assert.DoesNotContain("token\"", list.Stdout); // tokeny nigdy na wyjściu

        CliResult text = await Ksef("session");
        Assert.StartsWith($"test\t{nip}\t", text.Stdout);

        CliResult logout = await Ksef("logout");
        Assert.True(logout.ExitCode == 0, logout.Stderr);
        Assert.Equal(1, (await Ksef("session")).ExitCode);
    }

    [Fact]
    public async Task Login_CertificateFromStdin()
    {
        string nip = TestData.RandomNip();
        byte[] pem = File.ReadAllBytes(await NewCertificateAsync(nip));

        CliResult result = await Tests.Ksef.RunAsync(runtime.Path, pem, "login", "--nip", nip, "--cert", "-");

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Equal(nip, SessionStore.Resolve("test", null).Nip);
    }

    [Fact]
    public async Task Login_CertificateForAnotherNip_IsRejectedImmediately()
    {
        string certNip = TestData.RandomNip();
        string otherNip = TestData.RandomNip();
        string pem = await NewCertificateAsync(certNip);

        DateTime start = DateTime.UtcNow;
        CliResult result = await Ksef("login", "--nip", otherNip, "--cert", pem);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("415", result.Stderr);
        Assert.Contains(otherNip, result.Stderr);
        Assert.True(DateTime.UtcNow - start < TimeSpan.FromSeconds(30), "Odrzucenie nie powinno czekać do limitu czasu.");
        Assert.Empty(SessionStore.List());
    }

    [Fact]
    public async Task Login_SealCertificate()
    {
        string nip = TestData.RandomNip();
        string pem = Path.Combine(runtime.Path, "seal.pem");
        Assert.Equal(0, (await Ksef("cert", "new", "--seal", "--nip", nip, "-o", pem)).ExitCode);

        CliResult result = await Ksef("login", "--nip", nip, "--cert", pem);

        Assert.True(result.ExitCode == 0, result.Stderr);
    }

    [Fact]
    public async Task ExpiredAccessToken_IsRefreshed_AndLogoutRevokesRefreshToken()
    {
        string nip = TestData.RandomNip();
        await LoginAsync(nip);
        KsefSession session = SessionStore.Resolve("test", nip);

        await using ServiceProvider provider = KsefServices.Create(KsefEnvironment.Test);
        using IServiceScope scope = provider.CreateScope();
        IAuthorizationClient authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationClient>();

        // Udajemy, że access token wygasł: następne użycie sesji musi go odświeżyć w KSeF.
        KsefSession expired = session with { AccessToken = new() { Token = session.AccessToken.Token, ValidUntil = DateTimeOffset.UtcNow.AddMinutes(-1) } };
        SessionStore.Save(expired);
        string refreshed = await SessionStore.GetAccessTokenAsync(expired, authorization, TestContext.Current.CancellationToken);

        Assert.NotEqual(session.AccessToken.Token, refreshed);
        KsefSession saved = SessionStore.Resolve("test", nip);
        Assert.Equal(refreshed, saved.AccessToken.Token);
        Assert.True(saved.AccessToken.ValidUntil > DateTimeOffset.UtcNow.AddMinutes(1));

        // Wylogowanie unieważnia sesję w KSeF: refresh token przestaje działać.
        CliResult logout = await Ksef("logout", "--nip", nip);
        Assert.True(logout.ExitCode == 0, logout.Stderr);
        await Assert.ThrowsAsync<KsefApiException>(() => authorization.RefreshAccessTokenAsync(session.RefreshToken.Token, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TwoSessions_LogoutRequiresNip()
    {
        string first = TestData.RandomNip();
        string second = TestData.RandomNip();
        await LoginAsync(first);
        await LoginAsync(second);

        CliResult ambiguous = await Ksef("logout");
        Assert.Equal(1, ambiguous.ExitCode);
        Assert.Contains("--nip", ambiguous.Stderr);
        Assert.Equal(2, SessionStore.List("test").Count);

        Assert.Equal(0, (await Ksef("logout", "--nip", first)).ExitCode);
        Assert.Equal([second], SessionStore.List("test").Select(s => s.Nip));
        Assert.Equal(0, (await Ksef("logout")).ExitCode);
        Assert.Empty(SessionStore.List());
    }

    [Fact]
    public async Task List_NewNip_HasNoInvoicesInAnyRole()
    {
        await LoginAsync(TestData.RandomNip());

        foreach (string subject in new[] { "buyer", "seller", "third", "authorized" })
        {
            CliResult text = await Ksef("list", "--subject", subject);
            Assert.True(text.ExitCode == 0, $"{subject}: {text.Stderr}");
            Assert.Empty(text.StdoutBytes);
        }

        CliResult json = await Ksef("list", "--json");
        Assert.Equal(0, json.ExitCode);
        Assert.Equal("[]", json.Stdout.Trim());
    }

    [Theory]
    [InlineData("issue")]
    [InlineData("invoicing")]
    [InlineData("storage")]
    public async Task List_RangeLongerThanKsefLimit_IsSplitAutomatically(string dateType)
    {
        // KSeF odrzuca zakres ponad 100 dni (21405); rok musi przejść dzięki podziałowi na okna.
        await LoginAsync(TestData.RandomNip());
        string from = DateTime.Today.AddYears(-1).ToString("yyyy-MM-dd");

        CliResult result = await Ksef("list", "--date-type", dateType, "--from", from);

        Assert.True(result.ExitCode == 0, result.Stderr);
    }

    [Fact]
    public async Task List_RefreshesExpiredAccessToken()
    {
        string nip = TestData.RandomNip();
        await LoginAsync(nip);
        KsefSession session = SessionStore.Resolve("test", nip);
        SessionStore.Save(session with { AccessToken = new() { Token = session.AccessToken.Token, ValidUntil = DateTimeOffset.UtcNow.AddMinutes(-1) } });

        CliResult result = await Ksef("list");

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.NotEqual(session.AccessToken.Token, SessionStore.Resolve("test", nip).AccessToken.Token);
    }

    private string WriteInvoice(string name, byte[] xml)
    {
        string path = Path.Combine(runtime.Path, name);
        File.WriteAllBytes(path, xml);
        return path;
    }

    private static string[][] Rows(CliResult result) =>
        [.. result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split('\t'))];

    [Fact]
    public async Task Send_TwoInvoices_ReturnsKsefNumbersInInputOrder_AndListShowsThem()
    {
        string nip = TestData.RandomNip();
        await LoginAsync(nip);
        string first = WriteInvoice("a.xml", TestData.InvoiceToSend(nip));
        string second = WriteInvoice("b.xml", TestData.InvoiceToSend(nip));

        CliResult sent = await Ksef("send", first, second);

        Assert.True(sent.ExitCode == 0, sent.Stderr);
        Assert.Empty(sent.Stderr);
        string[][] rows = Rows(sent);
        Assert.Equal([first, second], rows.Select(r => r[1]));
        Assert.All(rows, r =>
        {
            Assert.StartsWith($"{nip}-", r[0]); // numer KSeF zaczyna się od NIP-u sprzedawcy
            Assert.Equal(rows[0][2], r[2]); // jedna sesja interaktywna
        });

        // Faktury są już widoczne w KSeF (data przyjęcia = dziś; data wystawienia we wzorze jest stara).
        CliResult listed = await Ksef("list", "--subject", "seller", "--date-type", "invoicing");
        Assert.True(listed.ExitCode == 0, listed.Stderr);
        Assert.Equal(rows.Select(r => r[0]).Order(), Rows(listed).Select(r => r[0]).Order());
        Assert.All(Rows(listed), r => Assert.Equal(nip, r[3]));
    }

    [Fact]
    public async Task Send_Duplicate_IsRejectedWithOriginalKsefNumber()
    {
        string nip = TestData.RandomNip();
        await LoginAsync(nip);
        string invoice = WriteInvoice("a.xml", TestData.InvoiceToSend(nip));
        string original = Rows(await Ksef("send", invoice))[0][0];
        string fresh = WriteInvoice("b.xml", TestData.InvoiceToSend(nip));

        // Duplikat obok nowej faktury: nowa przechodzi, duplikat odrzucony, kod wyjścia 1.
        CliResult result = await Ksef("send", invoice, fresh);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal([fresh], Rows(result).Select(r => r[1]));
        Assert.Contains($"{invoice}: KSeF 440", result.Stderr);
        Assert.Contains($"originalKsefNumber={original}", result.Stderr);
    }

    [Fact]
    public async Task Send_Json_DescribesAcceptedAndRejected()
    {
        string nip = TestData.RandomNip();
        await LoginAsync(nip);
        string invoice = WriteInvoice("a.xml", TestData.InvoiceToSend(nip));

        CliResult first = await Ksef("send", "--json", invoice);
        CliResult again = await Ksef("send", "--json", invoice);

        Assert.Equal(0, first.ExitCode);
        using JsonDocument accepted = JsonDocument.Parse(first.Stdout);
        JsonElement ok = Assert.Single(accepted.RootElement.EnumerateArray());
        Assert.True(ok.GetProperty("accepted").GetBoolean());
        Assert.Equal(200, ok.GetProperty("code").GetInt32());
        Assert.StartsWith($"{nip}-", ok.GetProperty("ksefNumber").GetString());
        Assert.False(ok.TryGetProperty("index", out _));

        Assert.Equal(1, again.ExitCode);
        using JsonDocument rejected = JsonDocument.Parse(again.Stdout);
        JsonElement duplicate = Assert.Single(rejected.RootElement.EnumerateArray());
        Assert.False(duplicate.GetProperty("accepted").GetBoolean());
        Assert.Equal(440, duplicate.GetProperty("code").GetInt32());
        Assert.Equal(ok.GetProperty("ksefNumber").GetString(), duplicate.GetProperty("extensions").GetProperty("originalKsefNumber").GetString());
    }

    [Fact]
    public async Task Send_FromStdin()
    {
        string nip = TestData.RandomNip();
        await LoginAsync(nip);

        CliResult result = await Tests.Ksef.RunAsync(runtime.Path, TestData.InvoiceToSend(nip), "send");

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Equal("<stdin>", Assert.Single(Rows(result))[1]);
    }

    [Fact]
    public async Task Send_SellerInvoice_AppearsOnBuyerSideByDefault()
    {
        // Sprzedawca wystawia fakturę na nabywcę; nabywca widzi ją w domyślnym `ksef list` (faktury otrzymane).
        string seller = TestData.RandomNip();
        string buyer = TestData.RandomNip();
        await LoginAsync(seller);
        string ksefNumber = Rows(await Ksef("send", "--nip", seller, WriteInvoice("a.xml", TestData.InvoiceToSend(seller, buyer))))[0][0];
        await LoginAsync(buyer);

        CliResult listed = await Ksef("list", "--nip", buyer, "--date-type", "invoicing");

        Assert.True(listed.ExitCode == 0, listed.Stderr);
        string[] row = Assert.Single(Rows(listed));
        Assert.Equal(ksefNumber, row[0]);
        Assert.Equal(seller, row[3]);
        Assert.Equal(buyer, row[5]);
    }
}
