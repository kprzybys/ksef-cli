using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Models.Authorization;

namespace KsefCli;

/// <summary>Sesja KSeF po zalogowaniu: tokeny dostępu dla jednego środowiska i NIP-u.</summary>
public sealed record KsefSession(string Environment, string Nip, string ReferenceNumber, TokenInfo AccessToken, TokenInfo RefreshToken);

/// <summary>
/// Sesje to stan narzędzia, nie trwały sekret: trzymamy je w `$XDG_RUNTIME_DIR/ksef/` (tmpfs, znika
/// po wylogowaniu z systemu), plik na sesję `&lt;środowisko&gt;-&lt;NIP&gt;.json` z prawami 0600.
/// </summary>
public static class SessionStore
{
    // Odświeżamy z zapasem, żeby token nie wygasł w trakcie żądania.
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions JsonOptions = KsefJson.Configure(new(JsonSerializerDefaults.Web) { WriteIndented = true }, SessionJsonContext.Default);

    private static JsonTypeInfo<KsefSession> SessionTypeInfo => (JsonTypeInfo<KsefSession>)JsonOptions.GetTypeInfo(typeof(KsefSession));

    public static string Directory
    {
        get
        {
            string runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } dir
                ? dir
                : throw new SessionException("Brak XDG_RUNTIME_DIR — nie ma gdzie trzymać sesji (zwykle ustawia go systemd przy logowaniu).");
            return Path.Combine(runtimeDir, "ksef");
        }
    }

    public static void Save(KsefSession session)
    {
        System.IO.Directory.CreateDirectory(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string path = PathFor(session.Environment, session.Nip);
        string temp = path + ".tmp";
        FileStreamOptions options = new()
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        using (FileStream stream = new(temp, options))
        {
            JsonSerializer.Serialize(stream, session, SessionTypeInfo);
        }
        File.Move(temp, path, overwrite: true);
    }

    public static IReadOnlyList<KsefSession> List(string? environment = null)
    {
        string directory = Directory;
        if (!System.IO.Directory.Exists(directory))
        {
            return [];
        }
        return [.. System.IO.Directory.EnumerateFiles(directory, "*.json")
            .Select(Read)
            .Where(s => environment is null || s.Environment == environment)
            .OrderBy(s => s.Environment).ThenBy(s => s.Nip)];
    }

    /// <summary>Sesja dla podanego NIP-u albo jedyna sesja w środowisku, gdy NIP nie podano.</summary>
    public static KsefSession Resolve(string environment, string? nip)
    {
        if (nip is not null)
        {
            string path = PathFor(environment, nip);
            return File.Exists(path)
                ? Read(path)
                : throw new SessionException($"Brak sesji dla NIP {nip} w środowisku {environment}. Zaloguj się: ksef login -e {environment} --nip {nip} --cert <plik>");
        }

        IReadOnlyList<KsefSession> sessions = List(environment);
        return sessions.Count switch
        {
            1 => sessions[0],
            0 => throw new SessionException($"Brak sesji w środowisku {environment}. Zaloguj się: ksef login -e {environment} --nip <NIP> --cert <plik>"),
            _ => throw new SessionException($"Kilka sesji w środowisku {environment} ({string.Join(", ", sessions.Select(s => s.Nip))}) — wskaż --nip."),
        };
    }

    public static void Delete(KsefSession session) => File.Delete(PathFor(session.Environment, session.Nip));

    /// <summary>Ważny access token sesji; odświeża go refresh tokenem, gdy zaraz wygaśnie.</summary>
    public static async Task<string> GetAccessTokenAsync(KsefSession session, IAuthorizationClient authorization, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (session.AccessToken.ValidUntil > now + RefreshMargin)
        {
            return session.AccessToken.Token;
        }
        if (session.RefreshToken.ValidUntil <= now + RefreshMargin)
        {
            throw new SessionException($"Sesja NIP {session.Nip} ({session.Environment}) wygasła. Zaloguj się ponownie: ksef login");
        }

        RefreshTokenResponse refreshed = await authorization.RefreshAccessTokenAsync(session.RefreshToken.Token, cancellationToken);
        Save(session with { AccessToken = refreshed.AccessToken });
        return refreshed.AccessToken.Token;
    }

    private static string PathFor(string environment, string nip) => Path.Combine(Directory, $"{environment}-{nip}.json");

    private static KsefSession Read(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, SessionTypeInfo)
            ?? throw new SessionException($"Uszkodzony plik sesji: {path}");
    }
}

/// <summary>Access token sesji na żądanie: komendy z wieloma żądaniami pytają przed każdym, odświeżenie zapisuje się raz.</summary>
public sealed class AccessTokenProvider(KsefSession session, IAuthorizationClient authorization)
{
    public KsefSession Session { get; private set; } = session;

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        string token = await SessionStore.GetAccessTokenAsync(Session, authorization, cancellationToken);
        if (token != Session.AccessToken.Token)
        {
            Session = SessionStore.Resolve(Session.Environment, Session.Nip);
        }
        return token;
    }
}

/// <summary>Błąd sesji do pokazania użytkownikowi wprost (brak, wygasła, niejednoznaczna).</summary>
public sealed class SessionException(string message) : Exception(message);
