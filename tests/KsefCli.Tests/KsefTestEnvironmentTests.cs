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
}
