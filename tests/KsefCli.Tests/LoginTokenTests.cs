using System.Text;
using System.Text.Json.Nodes;

namespace KsefCli.Tests;

/// <summary>`ksef login --token-file` bez sieci, przeciw <see cref="FakeKsef"/>: token, czas wyzwania i kontekst w żądaniu.</summary>
public class LoginTokenTests
{
    private const string Nip = "5908996669";
    private const string Reference = "20261030-AU-1234567890-ABCDEF0123-46";
    private const string Challenge = "20261030-CR-0123456789-ABCDEF0123-02";
    private const string Token = "20261030-EC-0123456789-ABCDEF0123-03|nip-5908996669|0123456789abcdef";
    private static readonly DateTimeOffset ChallengeTime = new(2026, 10, 30, 12, 0, 0, 123, TimeSpan.Zero);

    /// <summary>KSeF przyjmujący logowanie tokenem; `status` — wynik weryfikacji (domyślnie 200).</summary>
    private static FakeKsef AuthenticatingKsef(int status = 200, string description = "Uwierzytelnianie zakończone sukcesem")
    {
        FakeKsef ksef = new();
        DateTimeOffset until = DateTimeOffset.UtcNow.AddMinutes(15);
        ksef.On("POST", "auth/challenge", (_, _) => FakeResponse.Json(new
        {
            challenge = Challenge,
            timestamp = ChallengeTime,
            timestampMs = ChallengeTime.ToUnixTimeMilliseconds(),
            clientIp = "127.0.0.1",
        }));
        ksef.On("POST", "auth/ksef-token", (_, _) => FakeResponse.Json(new
        {
            referenceNumber = Reference,
            authenticationToken = new { token = "fake-authentication-token", validUntil = until },
        }));
        ksef.On("GET", $"auth/{Reference}", (_, _) => FakeResponse.Json(new
        {
            startDate = DateTimeOffset.UtcNow,
            status = new { code = status, description, details = Array.Empty<string>() },
        }));
        ksef.On("POST", "auth/token/redeem", (_, _) => FakeResponse.Json(new
        {
            accessToken = new { token = "fake-access-token", validUntil = until },
            refreshToken = new { token = "fake-refresh-token", validUntil = until.AddDays(7) },
        }));
        return ksef;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Login_Token_SendsEncryptedTokenWithChallengeTime_SavesSession(bool fromStdin)
    {
        using FakeKsef ksef = AuthenticatingKsef();
        using TempDir dir = new();
        // Plik z pustym wierszem na początku i końcem wiersza — tak go zostawia edytor albo `echo`.
        string file = dir.File("token");
        File.WriteAllText(file, $"\n{Token}\n");

        CliResult login = fromStdin
            ? await ksef.RunAsync(dir.Path, Encoding.UTF8.GetBytes($"{Token}\n"), "login", "--nip", Nip, "--token-file", "-")
            : await ksef.RunAsync(dir.Path, "login", "--nip", Nip, "--token-file", file);

        Assert.True(login.ExitCode == 0, login.Stderr);
        Assert.Empty(login.StdoutBytes);

        FakeRequest submission = Assert.Single(ksef.RequestsTo("POST", "auth/ksef-token"));
        Assert.Equal($"{Token}|{ChallengeTime.ToUnixTimeMilliseconds()}", FakeKsef.DecryptKsefToken(submission));
        JsonNode json = submission.Json!;
        Assert.Equal(Challenge, (string?)json["challenge"]);
        Assert.Equal(Nip, (string?)json["contextIdentifier"]!["value"]);
        Assert.Equal("KsefTokenEncryption", (string?)json["publicKeyId"]);
        // Token w jawnej postaci nie wychodzi poza zaszyfrowane pole.
        Assert.DoesNotContain(ksef.Requests, r => r.Body.Contains("0123456789abcdef"));

        CliResult session = await ksef.RunAsync(dir.Path, "session", "--json");
        Assert.Equal(0, session.ExitCode);
        JsonNode saved = JsonNode.Parse(session.Stdout)!.AsArray().Single()!;
        Assert.Equal(Nip, (string?)saved["nip"]);
        Assert.Equal(Reference, (string?)saved["referenceNumber"]);
    }

    [Fact]
    public async Task Login_Token_Rejected_ShowsKsefReasonAndSavesNothing()
    {
        using FakeKsef ksef = AuthenticatingKsef(450, "Uwierzytelnianie zakończone niepowodzeniem z powodu błędnego tokenu");
        using TempDir dir = new();
        string file = dir.File("token");
        File.WriteAllText(file, Token);

        CliResult login = await ksef.RunAsync(dir.Path, "login", "--nip", Nip, "--token-file", file);

        Assert.Equal(1, login.ExitCode);
        Assert.Equal(
            $"ksef: logowanie NIP {Nip} (test) tokenem KSeF nieudane: KSeF: 450 Uwierzytelnianie zakończone niepowodzeniem z powodu błędnego tokenu\n"
            + "ksef: token działa tylko w środowisku, w którym go wygenerowano; to jest test (piaskownica MF, fikcyjne dane). "
            + $"Token dla prawdziwej firmy: ksef -e prod login --nip {Nip} --token-file …\n",
            login.Stderr);
        Assert.Empty(ksef.RequestsTo("POST", "auth/token/redeem"));
        Assert.Equal(1, (await ksef.RunAsync(dir.Path, "session")).ExitCode);
    }

    [Fact]
    public async Task Login_Token_RejectedOnProduction_HasNoEnvironmentHint()
    {
        using FakeKsef ksef = AuthenticatingKsef(450, "Uwierzytelnianie zakończone niepowodzeniem z powodu błędnego tokenu");
        using TempDir dir = new();
        string file = dir.File("token");
        File.WriteAllText(file, Token);

        CliResult login = await ksef.RunAsync(dir.Path, "-e", "prod", "login", "--nip", Nip, "--token-file", file);

        Assert.Equal(1, login.ExitCode);
        Assert.DoesNotContain("-e prod", login.Stderr);
        Assert.Single(login.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }
}

public class LoginTokenOfflineTests
{
    private const string Nip = "5908996669";

    [Fact]
    public async Task Login_TokenGivenInsteadOfFile_DoesNotEchoIt()
    {
        using TempDir dir = new();
        const string token = "20261030-EC-0123456789-ABCDEF0123-03|nip-5908996669|secretsecret";

        CliResult result = await Ksef.RunAsync(dir.Path, "login", "--nip", Nip, "--token-file", token);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("nie sam token", result.Stderr);
        Assert.DoesNotContain("secretsecret", result.Stderr);
    }

    [Fact]
    public async Task Login_EmptyTokenFile_IsRejectedBeforeNetwork()
    {
        using TempDir dir = new();
        string file = dir.File("token");
        File.WriteAllText(file, "\n  \n");

        CliResult result = await Ksef.RunAsync(dir.Path, "login", "--nip", Nip, "--token-file", file);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal($"ksef: {file}: brak tokenu KSeF.\n", result.Stderr);
    }

    [Theory]
    [InlineData]
    [InlineData("--cert", "a.pem", "--token-file", "t")]
    [InlineData("--request", "--signed", "s.xml")]
    [InlineData("--cert", "a.pem", "--request")]
    public async Task Login_RequiresExactlyOneMethod(params string[] method)
    {
        using TempDir dir = new();

        CliResult result = await Ksef.RunAsync(dir.Path, ["login", "--nip", Nip, .. method]);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("dokładnie jedną metodę: --cert (certyfikat), --token-file (token KSeF), --request albo --signed", result.Stderr);
    }
}
