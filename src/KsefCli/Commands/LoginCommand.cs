using System.CommandLine;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using KSeF.Client.Api.Builders.Auth;
using KSeF.Client.Api.Services;
using KSeF.Client.Core.Exceptions;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using KSeF.Client.Core.Models;
using KSeF.Client.Core.Models.ApiResponses;
using KSeF.Client.Core.Models.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace KsefCli.Commands;

/// <summary>
/// `ksef login` — uwierzytelnienie w kontekście NIP-u certyfikatem (podpis XAdES żądania z wyzwaniem KSeF)
/// albo tokenem KSeF (zaszyfrowanym kluczem publicznym MF razem z czasem wyzwania); tokeny sesji zapisuje
/// w <see cref="SessionStore"/>. Przy sukcesie milczy. Token KSeF to sekret — tylko z pliku albo stdin,
/// nigdy z argumentu (widać go w `ps` i historii powłoki).
/// </summary>
public static partial class LoginCommand
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(2);

    public static Command Create(Option<string> envOption)
    {
        Option<string> nipOption = Nip.Option("NIP firmy (kontekst logowania)");
        nipOption.Required = true;
        Option<string> certOption = new("--cert")
        {
            Description = "Plik PEM z certyfikatem i kluczem prywatnym (z --key: sam certyfikat); `-` = stdin",
        };
        Option<string> keyOption = new("--key")
        {
            Description = "Klucz prywatny na karcie, tokenie albo w TPM: URI PKCS#11 (RFC 7512), np. `pkcs11:token=…;object=…;type=private`",
        };
        Option<string> pinFileOption = new("--pin-file")
        {
            Description = "Plik z PIN-em do --key (pierwszy niepusty wiersz); `-` = stdin",
        };
        Option<string> keyViaOption = new("--key-via")
        {
            Description = "Dostęp OpenSSL do --key: engine (libp11) albo provider (pkcs11-provider); domyślnie engine na OpenSSL < 3.2, provider na nowszym",
        };
        keyViaOption.AcceptOnlyFromAmong("engine", "provider");
        Option<string> tokenOption = new("--token-file")
        {
            Description = "Plik z tokenem KSeF (pierwszy niepusty wiersz); `-` = stdin, np. `pass show ksef/token | ksef login --token-file -`",
        };

        Option<bool> requestOption = new("--request")
        {
            Description = "Wypisz na stdout niepodpisane żądanie logowania (XML z wyzwaniem KSeF) do podpisania innym programem, np. Profilem Zaufanym",
        };
        Option<string> signedOption = new("--signed")
        {
            Description = "Wyślij żądanie z --request podpisane innym programem (XAdES); `-` = stdin",
        };

        Command command = new("login", "Logowanie do KSeF certyfikatem (XAdES), tokenem KSeF albo żądaniem podpisanym innym programem; sesja trafia do " + (OperatingSystem.IsMacOS() ? "$TMPDIR/ksef" : "$XDG_RUNTIME_DIR/ksef"));
        command.Options.Add(nipOption);
        command.Options.Add(certOption);
        command.Options.Add(tokenOption);
        command.Options.Add(requestOption);
        command.Options.Add(signedOption);
        command.Options.Add(keyOption);
        command.Options.Add(pinFileOption);
        command.Options.Add(keyViaOption);
        command.Validators.Add(result =>
        {
            int methods = new[] { result.GetValue(certOption), result.GetValue(tokenOption), result.GetValue(signedOption) }.Count(v => v is not null)
                + (result.GetValue(requestOption) ? 1 : 0);
            if (methods == 1 && result.GetValue(keyOption) is null && (result.GetValue(pinFileOption) ?? result.GetValue(keyViaOption)) is not null)
            {
                result.AddError("--pin-file i --key-via dotyczą tylko klucza na karcie (--key).");
            }
            if (result.GetValue(keyOption) is { } keyUri && HasPinInUri(keyUri))
            {
                result.AddError("PIN podaj przez --pin-file, nie w --key (pin-value/pin-source trafiłyby do komunikatów i historii powłoki).");
            }
            if (result.GetValue(keyOption) is not null && result.GetValue(certOption) is null)
            {
                result.AddError("--key wymaga --cert z certyfikatem (sam certyfikat, bez klucza).");
            }
            if (string.Equals(result.GetValue(certOption), CliIo.StdStream, StringComparison.Ordinal)
                && string.Equals(result.GetValue(pinFileOption), CliIo.StdStream, StringComparison.Ordinal))
            {
                result.AddError("Na stdin może iść tylko jedno: certyfikat albo PIN.");
            }
            if (methods != 1)
            {
                result.AddError("Podaj dokładnie jedną metodę: --cert (certyfikat), --token-file (token KSeF), --request albo --signed (podpis innym programem).");
            }
        });
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            KsefEnvironment env = KsefEnvironment.Parse(parseResult.GetRequiredValue(envOption));
            string nip = parseResult.GetRequiredValue(nipOption);
            if (parseResult.GetValue(requestOption))
            {
                return await WriteRequestAsync(env, nip, cancellationToken);
            }
            if (parseResult.GetValue(signedOption) is { } signedPath)
            {
                return await RunWithSignedRequestAsync(env, nip, signedPath, cancellationToken);
            }
            return parseResult.GetValue(tokenOption) is { } tokenPath
                ? await RunWithTokenAsync(env, nip, tokenPath, cancellationToken)
                : await RunAsync(
                    env, nip, parseResult.GetRequiredValue(certOption),
                    parseResult.GetValue(keyOption) is { } key ? new HardwareKey(key, parseResult.GetValue(pinFileOption), parseResult.GetValue(keyViaOption)) : null,
                    cancellationToken);
        });
        return command;
    }

    private static bool CanSaveSession()
    {
        try
        {
            // Najpierw miejsce na sesję: bez niego udane logowanie zostawiłoby w KSeF sesję bez tokenów u nas.
            _ = SessionStore.Directory;
            return true;
        }
        catch (SessionException ex)
        {
            CliIo.Error(ex.Message);
            return false;
        }
    }

    private static async Task<int> RunWithTokenAsync(KsefEnvironment env, string nip, string tokenPath, CancellationToken cancellationToken)
    {
        if (!CanSaveSession())
        {
            return 1;
        }

        string? token;
        try
        {
            token = Encoding.UTF8.GetString(CliIo.ReadAllBytes(tokenPath))
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Bez ścieżki w komunikacie: ktoś mógł podać tu sam token zamiast pliku.
            CliIo.Error("--token-file: nie ma takiego pliku — podaj plik z tokenem albo `-` (stdin), nie sam token.");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CliIo.Error($"--token-file: nie udało się wczytać tokenu: {ex.Message}");
            return 1;
        }
        if (token is null)
        {
            CliIo.Error($"{CliIo.DisplayName(tokenPath)}: brak tokenu KSeF.");
            return 1;
        }

        try
        {
            await using ServiceProvider provider = await KsefServices.CreateWithCryptographyAsync(env, cancellationToken);
            using IServiceScope scope = provider.CreateScope();
            IAuthorizationClient authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationClient>();
            ICryptographyService crypto = scope.ServiceProvider.GetRequiredService<ICryptographyService>();

            AuthenticationChallengeResponse challenge = await authorization.GetAuthChallengeAsync(cancellationToken);
            // Token z czasem wyzwania, zaszyfrowany kluczem MF (RSA-OAEP SHA-256) — KSeF odrzuca powtórzone żądania.
            byte[] encrypted = crypto.EncryptKsefTokenWithRSAUsingPublicKey(
                Encoding.UTF8.GetBytes($"{token}|{challenge.Timestamp.ToUnixTimeMilliseconds()}"));
            IAuthKsefTokenRequestBuilderWithEncryptedToken request = AuthKsefTokenRequestBuilder.Create()
                .WithChallenge(challenge.Challenge)
                .WithContext(AuthenticationTokenContextIdentifierType.Nip, nip)
                .WithEncryptedToken(Convert.ToBase64String(encrypted));
            if (crypto.KsefTokenPublicKeyId is { } publicKeyId)
            {
                request = request.WithPublicKeyId(publicKeyId);
            }

            SignatureResponse submission = await authorization.SubmitKsefTokenAuthRequestAsync(request.Build(), cancellationToken);
            SessionStore.Save(await CompleteAsync(authorization, env, nip, submission, cancellationToken));
            return 0;
        }
        catch (Exception ex) when (ex is KsefApiException or HttpRequestException or AuthenticationFailedException or SessionException or ArgumentException)
        {
            CliIo.Error($"logowanie NIP {nip} ({env.Name}) tokenem KSeF nieudane: {ex.Message}");
            if (ex is AuthenticationFailedException { Code: AuthenticationStatusCodeResponse.InvalidToken } && env.Name != KsefEnvironment.Prod.Name)
            {
                // Najczęstsza pułapka nowego użytkownika: token z Aplikacji Podatnika (produkcja), komenda bez -e (test).
                // Każde środowisko MF ma własne tokeny.
                CliIo.Error($"token działa tylko w środowisku, w którym go wygenerowano; to jest {env.Name} ({env.Description}). "
                    + $"Token dla prawdziwej firmy: ksef -e prod login --nip {nip} --token-file …");
            }
            return 1;
        }
    }

    private static async Task<int> RunAsync(KsefEnvironment env, string nip, string certPath, HardwareKey? key, CancellationToken cancellationToken)
    {
        if (!CanSaveSession())
        {
            return 1;
        }

        X509Certificate2 certificate;
        try
        {
            string pem = Encoding.ASCII.GetString(CliIo.ReadAllBytes(certPath));
            certificate = key is null ? X509Certificate2.CreateFromPem(pem, pem) : X509Certificate2.CreateFromPem(pem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            CliIo.Error($"{CliIo.DisplayName(certPath)}: nie udało się wczytać certyfikatu{(key is null ? " z kluczem" : "")} (PEM): {ex.Message}");
            return 1;
        }
        AttachedKey? hardware = null;
        if (key is not null)
        {
            using X509Certificate2 publicOnly = certificate;
            if (!TryAttachHardwareKey(publicOnly, key, out hardware))
            {
                return 1;
            }
            certificate = hardware.Certificate;
        }
        Func<string, string> sign = hardware is null
            ? xml => SignatureService.Sign(xml, certificate)
            : xml => SignWithHardwareKey(xml, hardware);

        using (certificate)
        {
            await using ServiceProvider provider = KsefServices.Create(env);
            using IServiceScope scope = provider.CreateScope();
            IAuthorizationClient authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationClient>();

            try
            {
                KsefSession session = await AuthenticateAsync(authorization, env, nip, sign, cancellationToken);
                SessionStore.Save(session);
                return 0;
            }
            // ArgumentException: walidacja danych żądania w bibliotece MF (np. identyfikatora kontekstu).
            catch (Exception ex) when (ex is KsefApiException or HttpRequestException or AuthenticationFailedException or SessionException or ArgumentException)
            {
                CliIo.Error($"logowanie NIP {nip} ({env.Name}) nieudane: {ex.Message}");
                return 1;
            }
            catch (HardwareKeyException ex)
            {
                CliIo.Error($"logowanie NIP {nip} ({env.Name}) nieudane — podpis kluczem {key?.Uri}: {ex.Message}");
                return 1;
            }
        }
    }

    private static async Task<KsefSession> AuthenticateAsync(
        IAuthorizationClient authorization, KsefEnvironment env, string nip, Func<string, string> sign, CancellationToken cancellationToken)
    {
        string signedXml = sign(await NewRequestXmlAsync(authorization, nip, cancellationToken));

        SignatureResponse submission = await authorization.SubmitXadesAuthRequestAsync(signedXml, cancellationToken: cancellationToken);
        return await CompleteAsync(authorization, env, nip, submission, cancellationToken);
    }

    /// <summary>Żądanie logowania XAdES z nowym wyzwaniem KSeF — do podpisania certyfikatem albo innym programem.</summary>
    private static async Task<string> NewRequestXmlAsync(IAuthorizationClient authorization, string nip, CancellationToken cancellationToken)
    {
        AuthenticationChallengeResponse challenge = await authorization.GetAuthChallengeAsync(cancellationToken);
        AuthenticationTokenRequest request = AuthTokenRequestBuilder.Create()
            .WithChallenge(challenge.Challenge)
            .WithContext(AuthenticationTokenContextIdentifierType.Nip, nip)
            .WithIdentifierType(AuthenticationTokenSubjectIdentifierTypeEnum.CertificateSubject)
            .Build();
        return AuthenticationTokenRequestSerializer.SerializeToXmlString(request);
    }

    /// <summary>Wspólny koniec wszystkich metod: czekanie na weryfikację w KSeF i wymiana na tokeny sesji.</summary>
    private static async Task<KsefSession> CompleteAsync(
        IAuthorizationClient authorization, KsefEnvironment env, string nip, SignatureResponse submission, CancellationToken cancellationToken)
    {
        await WaitForCompletionAsync(authorization, submission, cancellationToken);

        AuthenticationOperationStatusResponse tokens = await authorization.GetAccessTokenAsync(submission.AuthenticationToken.Token, cancellationToken);
        return new KsefSession(env.Name, nip, submission.ReferenceNumber, tokens.AccessToken, tokens.RefreshToken);
    }

    // Własne odpytywanie zamiast IAuthCoordinator: ten czeka do limitu czasu także wtedy, gdy KSeF
    // już odrzucił logowanie (kod 4xx), a my chcemy od razu pokazać powód.
    private static async Task WaitForCompletionAsync(IAuthorizationClient authorization, SignatureResponse submission, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + PollTimeout;
        while (true)
        {
            AuthStatus status = await authorization.GetAuthStatusAsync(submission.ReferenceNumber, submission.AuthenticationToken.Token, cancellationToken);
            switch (status.Status.Code)
            {
                case AuthenticationStatusCodeResponse.AuthenticationSuccess:
                    return;
                case AuthenticationStatusCodeResponse.AuthenticationInProgress when DateTime.UtcNow < deadline:
                    await Task.Delay(PollInterval, cancellationToken);
                    break;
                default:
                    string details = status.Status.Details is { Count: > 0 } d ? $" ({string.Join("; ", d)})" : "";
                    throw new AuthenticationFailedException(status.Status.Code, $"KSeF: {status.Status.Code} {status.Status.Description}{details}");
            }
        }
    }

    private sealed class AuthenticationFailedException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
