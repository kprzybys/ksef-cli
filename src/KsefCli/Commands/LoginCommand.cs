using System.CommandLine;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using KSeF.Client.Api.Builders.Auth;
using KSeF.Client.Api.Services;
using KSeF.Client.Core.Exceptions;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Models;
using KSeF.Client.Core.Models.ApiResponses;
using KSeF.Client.Core.Models.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace KsefCli.Commands;

/// <summary>
/// `ksef login` — uwierzytelnienie certyfikatem (podpis XAdES żądania z wyzwaniem KSeF)
/// w kontekście NIP-u; tokeny sesji zapisuje w <see cref="SessionStore"/>. Przy sukcesie milczy.
/// </summary>
public static class LoginCommand
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(2);

    public static Command Create(Option<string> envOption)
    {
        Option<string> nipOption = Nip.Option("NIP firmy (kontekst logowania)");
        nipOption.Required = true;
        Option<string> certOption = new("--cert")
        {
            Description = "Plik PEM z certyfikatem i kluczem prywatnym; `-` = stdin",
            Required = true,
        };

        Command command = new("login", "Logowanie do KSeF certyfikatem (XAdES); sesja trafia do $XDG_RUNTIME_DIR/ksef");
        command.Options.Add(nipOption);
        command.Options.Add(certOption);
        command.SetAction(async (parseResult, cancellationToken) => await RunAsync(
            KsefEnvironment.Parse(parseResult.GetRequiredValue(envOption)),
            parseResult.GetRequiredValue(nipOption),
            parseResult.GetRequiredValue(certOption),
            cancellationToken));
        return command;
    }

    private static async Task<int> RunAsync(KsefEnvironment env, string nip, string certPath, CancellationToken cancellationToken)
    {
        X509Certificate2 certificate;
        try
        {
            string pem = Encoding.ASCII.GetString(CliIo.ReadAllBytes(certPath));
            certificate = X509Certificate2.CreateFromPem(pem, pem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            CliIo.Error($"{CliIo.DisplayName(certPath)}: nie udało się wczytać certyfikatu z kluczem (PEM): {ex.Message}");
            return 1;
        }

        using (certificate)
        {
            await using ServiceProvider provider = KsefServices.Create(env);
            using IServiceScope scope = provider.CreateScope();
            IAuthorizationClient authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationClient>();

            try
            {
                KsefSession session = await AuthenticateAsync(authorization, env, nip, certificate, cancellationToken);
                SessionStore.Save(session);
                return 0;
            }
            // ArgumentException: walidacja danych żądania w bibliotece MF (np. identyfikatora kontekstu).
            catch (Exception ex) when (ex is KsefApiException or HttpRequestException or AuthenticationFailedException or SessionException or ArgumentException)
            {
                CliIo.Error($"logowanie NIP {nip} ({env.Name}) nieudane: {ex.Message}");
                return 1;
            }
        }
    }

    private static async Task<KsefSession> AuthenticateAsync(
        IAuthorizationClient authorization, KsefEnvironment env, string nip, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        AuthenticationChallengeResponse challenge = await authorization.GetAuthChallengeAsync(cancellationToken);

        AuthenticationTokenRequest request = AuthTokenRequestBuilder.Create()
            .WithChallenge(challenge.Challenge)
            .WithContext(AuthenticationTokenContextIdentifierType.Nip, nip)
            .WithIdentifierType(AuthenticationTokenSubjectIdentifierTypeEnum.CertificateSubject)
            .Build();
        string signedXml = SignatureService.Sign(AuthenticationTokenRequestSerializer.SerializeToXmlString(request), certificate);

        SignatureResponse submission = await authorization.SubmitXadesAuthRequestAsync(signedXml, cancellationToken: cancellationToken);
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
                    throw new AuthenticationFailedException($"KSeF: {status.Status.Code} {status.Status.Description}{details}");
            }
        }
    }

    private sealed class AuthenticationFailedException(string message) : Exception(message);
}
