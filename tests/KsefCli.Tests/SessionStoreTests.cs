using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Models;
using KSeF.Client.Core.Models.Authorization;

namespace KsefCli.Tests;

[Collection(ProcessEnvironmentCollection.Name)]
public class SessionStoreTests
{
    private static KsefSession Session(string env = "test", string nip = "5908996669", TimeSpan? access = null, TimeSpan? refresh = null) =>
        new(env, nip, "20261030-AU-0000000000-0000000000-00",
            new TokenInfo { Token = "access-" + nip, ValidUntil = DateTimeOffset.UtcNow + (access ?? TimeSpan.FromMinutes(15)) },
            new TokenInfo { Token = "refresh-" + nip, ValidUntil = DateTimeOffset.UtcNow + (refresh ?? TimeSpan.FromDays(7)) });

    [Fact]
    public void Save_ThenResolve_RoundTrips()
    {
        using RuntimeDirScope scope = new();
        KsefSession session = Session();

        SessionStore.Save(session);
        KsefSession loaded = SessionStore.Resolve("test", "5908996669");

        Assert.Equal(session.ReferenceNumber, loaded.ReferenceNumber);
        Assert.Equal(session.AccessToken.Token, loaded.AccessToken.Token);
        Assert.Equal(session.RefreshToken.ValidUntil, loaded.RefreshToken.ValidUntil);
    }

    [Fact]
    public void Save_RestrictsPermissionsToOwner()
    {
        using RuntimeDirScope scope = new();
        SessionStore.Save(Session());

        string dir = Path.Combine(scope.Path, "ksef");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir, "test-5908996669.json")));
        Assert.Single(Directory.GetFiles(dir)); // bez pozostałości po zapisie przez plik tymczasowy
    }

    [Fact]
    public void Save_OverwritesExistingSession()
    {
        using RuntimeDirScope scope = new();
        SessionStore.Save(Session());
        SessionStore.Save(Session() with { ReferenceNumber = "nowa" });

        Assert.Equal("nowa", SessionStore.Resolve("test", null).ReferenceNumber);
    }

    [Fact]
    public void Resolve_WithoutNip_PicksTheOnlySessionInEnvironment()
    {
        using RuntimeDirScope scope = new();
        SessionStore.Save(Session("test", "5908996669"));
        SessionStore.Save(Session("demo", "1111111111"));

        Assert.Equal("5908996669", SessionStore.Resolve("test", null).Nip);
        Assert.Equal("1111111111", SessionStore.Resolve("demo", null).Nip);
    }

    [Fact]
    public void Resolve_WithoutNip_FailsWhenAmbiguous()
    {
        using RuntimeDirScope scope = new();
        SessionStore.Save(Session(nip: "5908996669"));
        SessionStore.Save(Session(nip: "1111111111"));

        SessionException ex = Assert.Throws<SessionException>(() => SessionStore.Resolve("test", null));
        Assert.Contains("--nip", ex.Message);
        Assert.Contains("1111111111", ex.Message);
    }

    [Fact]
    public void Resolve_FailsWhenNoSession()
    {
        using RuntimeDirScope scope = new();

        Assert.Throws<SessionException>(() => SessionStore.Resolve("test", null));
        Assert.Throws<SessionException>(() => SessionStore.Resolve("test", "5908996669"));
    }

    [Fact]
    public void List_FiltersByEnvironmentAndSorts()
    {
        using RuntimeDirScope scope = new();
        SessionStore.Save(Session("test", "5908996669"));
        SessionStore.Save(Session("test", "1111111111"));
        SessionStore.Save(Session("prod", "5260250995"));

        Assert.Equal(["1111111111", "5908996669"], SessionStore.List("test").Select(s => s.Nip));
        Assert.Equal(["prod", "test", "test"], SessionStore.List().Select(s => s.Environment));
    }

    [Fact]
    public void List_IsEmptyBeforeFirstLogin()
    {
        using RuntimeDirScope scope = new();
        Assert.Empty(SessionStore.List());
    }

    [Fact]
    public void Delete_RemovesOnlyThatSession()
    {
        using RuntimeDirScope scope = new();
        KsefSession kept = Session(nip: "1111111111");
        SessionStore.Save(Session());
        SessionStore.Save(kept);

        SessionStore.Delete(Session());

        Assert.Equal(["1111111111"], SessionStore.List().Select(s => s.Nip));
    }

    [Fact]
    public void Directory_RequiresXdgRuntimeDir()
    {
        using RuntimeDirScope scope = new();
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", null);

        if (OperatingSystem.IsMacOS())
        {
            Assert.StartsWith(Environment.GetEnvironmentVariable("TMPDIR")!, SessionStore.Directory, StringComparison.Ordinal);
        }
        else
        {
            Assert.Throws<SessionException>(() => SessionStore.Directory);
        }
    }

    [Fact]
    public async Task GetAccessToken_ReturnsCurrentTokenWithoutCallingKsef()
    {
        using RuntimeDirScope scope = new();
        FakeAuthorization authorization = new();

        string token = await SessionStore.GetAccessTokenAsync(Session(), authorization, CancellationToken.None);

        Assert.Equal("access-5908996669", token);
        Assert.Equal(0, authorization.RefreshCalls);
    }

    [Fact]
    public async Task GetAccessToken_RefreshesTokenAboutToExpireAndSavesIt()
    {
        using RuntimeDirScope scope = new();
        KsefSession session = Session(access: TimeSpan.FromSeconds(30));
        SessionStore.Save(session);
        FakeAuthorization authorization = new();

        string token = await SessionStore.GetAccessTokenAsync(session, authorization, CancellationToken.None);

        Assert.Equal("odswiezony", token);
        Assert.Equal(["refresh-5908996669"], authorization.RefreshedWith);
        KsefSession saved = SessionStore.Resolve("test", "5908996669");
        Assert.Equal("odswiezony", saved.AccessToken.Token);
        Assert.Equal(session.RefreshToken.Token, saved.RefreshToken.Token);
    }

    [Fact]
    public async Task GetAccessToken_FailsWhenRefreshTokenExpired()
    {
        using RuntimeDirScope scope = new();
        FakeAuthorization authorization = new();
        KsefSession session = Session(access: TimeSpan.FromMinutes(-5), refresh: TimeSpan.FromSeconds(10));

        SessionException ex = await Assert.ThrowsAsync<SessionException>(
            () => SessionStore.GetAccessTokenAsync(session, authorization, CancellationToken.None));

        Assert.Contains("ksef login", ex.Message);
        Assert.Equal(0, authorization.RefreshCalls);
    }

    /// <summary>Klient uwierzytelniania, który umie tylko odświeżyć token i zapamiętuje wywołania.</summary>
    private sealed class FakeAuthorization : IAuthorizationClient
    {
        public List<string> RefreshedWith { get; } = [];

        public int RefreshCalls => RefreshedWith.Count;

        public Task<RefreshTokenResponse> RefreshAccessTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
        {
            RefreshedWith.Add(refreshToken);
            return Task.FromResult(new RefreshTokenResponse
            {
                AccessToken = new TokenInfo { Token = "odswiezony", ValidUntil = DateTimeOffset.UtcNow.AddMinutes(15) },
            });
        }

        public Task<AuthenticationChallengeResponse> GetAuthChallengeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SignatureResponse> SubmitXadesAuthRequestAsync(string signedXML, bool verifyCertificateChain = false, bool enforceXadesCompliance = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SignatureResponse> SubmitKsefTokenAuthRequestAsync(AuthenticationKsefTokenRequest requestPayload, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AuthStatus> GetAuthStatusAsync(string authOperationReferenceNumber, string authenticationToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AuthenticationOperationStatusResponse> GetAccessTokenAsync(string authenticationToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
