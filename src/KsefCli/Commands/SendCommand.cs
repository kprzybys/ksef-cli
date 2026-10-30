using System.CommandLine;
using Ksef.Fa3;
using KSeF.Client.Api.Builders.Online;
using KSeF.Client.Core.Exceptions;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using KSeF.Client.Core.Models.Invoices;
using KSeF.Client.Core.Models.Sessions;
using KSeF.Client.Core.Models.Sessions.OnlineSession;
using Microsoft.Extensions.DependencyInjection;

namespace KsefCli.Commands;

/// <summary>
/// `ksef send` — wysyłka faktur FA(3) w sesji interaktywnej i oczekiwanie na numer KSeF.
/// Przed wysyłką ta sama kontrola co `ksef validate`; jeśli któryś plik jej nie przejdzie, nie wysyłamy nic.
/// Wysyłamy dokładnie bajty z pliku — KSeF porównuje ich skrót SHA-256 i rozmiar.
/// Wynik (stdout, TSV): numer KSeF, plik, numer referencyjny sesji (do pobrania UPO). Odrzucone — na stderr.
/// </summary>
public static class SendCommand
{
    // Kody statusu faktury w sesji: 100 przyjęta do przetwarzania, 150 w przetwarzaniu, 200 przyjęta (ma numer KSeF).
    private const int Accepted = 200;
    private static readonly int[] InProgress = [100, 150];
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(5);

    // Index: pozycja pliku na wejściu — ten sam plik może wystąpić dwa razy.
    // internal: typ wyniku --json musi być widoczny dla CliJsonContext.
    internal sealed record SendResult(
        int Index, string File, string SessionReferenceNumber, string? InvoiceReferenceNumber, string? KsefNumber,
        int? Code, string? Description, IReadOnlyCollection<string>? Details, IDictionary<string, string>? Extensions)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public int Index { get; init; } = Index;

        public bool Accepted => KsefNumber is not null;
    }

    public static Command Create(Option<string> envOption)
    {
        Argument<string[]> filesArgument = new("pliki")
        {
            Description = "Pliki XML faktur FA(3); `-` albo brak = stdin",
            Arity = ArgumentArity.ZeroOrMore,
        };
        Option<string> nipOption = Nip.Option("NIP sesji (wymagany, gdy w środowisku jest kilka sesji)");
        Option<bool> jsonOption = CliIo.JsonOption();

        Command command = new("send", "Wysyła faktury FA(3) do KSeF (jedna sesja interaktywna) i czeka na numery KSeF. Kod wyjścia: 0 wszystkie przyjęte, 1 jest błąd lub odrzucenie");
        command.Arguments.Add(filesArgument);
        command.Options.Add(nipOption);
        command.Options.Add(jsonOption);
        command.SetAction(async (parseResult, cancellationToken) => await RunAsync(
            KsefEnvironment.Parse(parseResult.GetRequiredValue(envOption)),
            parseResult.GetValue(nipOption),
            parseResult.GetValue(filesArgument) is { Length: > 0 } files ? files : [CliIo.StdStream],
            parseResult.GetValue(jsonOption),
            cancellationToken));
        return command;
    }

    private static async Task<int> RunAsync(KsefEnvironment env, string? nip, string[] files, bool json, CancellationToken cancellationToken)
    {
        List<(string Name, byte[] Xml)> invoices = [];
        bool invalid = false;
        foreach (string file in files)
        {
            string name = CliIo.DisplayName(file);
            byte[] xml;
            try
            {
                xml = CliIo.ReadAllBytes(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CliIo.Error($"{name}: {ex.Message}");
                invalid = true;
                continue;
            }
            foreach (Fa3ValidationError error in Fa3Validator.Validate(xml))
            {
                Console.Error.WriteLine($"{name}:{error.Line}:{error.Column}: {error.Source} błąd: {error.Message}");
                invalid = true;
            }
            invoices.Add((name, xml));
        }
        if (invalid)
        {
            CliIo.Error("nic nie wysłano — popraw pliki (ksef validate).");
            return 1;
        }

        List<SendResult> results;
        try
        {
            KsefSession session = SessionStore.Resolve(env.Name, nip);
            await using ServiceProvider provider = await KsefServices.CreateWithCryptographyAsync(env, cancellationToken);
            using IServiceScope scope = provider.CreateScope();
            AccessTokenProvider tokens = new(session, scope.ServiceProvider.GetRequiredService<IAuthorizationClient>());
            results = await SendAsync(
                scope.ServiceProvider.GetRequiredService<IKSeFClient>(),
                scope.ServiceProvider.GetRequiredService<ICryptographyService>(),
                tokens, invoices, cancellationToken);
        }
        catch (Exception ex) when (ex is SessionException or KsefApiException or HttpRequestException)
        {
            CliIo.Error(ex.Message);
            return 1;
        }

        if (json)
        {
            CliIo.WriteJson(results);
        }
        foreach (SendResult result in results)
        {
            if (result.Accepted)
            {
                if (!json)
                {
                    Console.Out.WriteLine($"{result.KsefNumber}\t{result.File}\t{result.SessionReferenceNumber}");
                }
                continue;
            }
            string details = result.Details is { Count: > 0 } d ? $" ({string.Join("; ", d)})" : "";
            string extensions = result.Extensions is { Count: > 0 } e ? $" [{string.Join(", ", e.Select(kv => $"{kv.Key}={kv.Value}"))}]" : "";
            CliIo.Error($"{result.File}: KSeF {result.Code} {result.Description}{details}{extensions}");
        }
        return results.All(r => r.Accepted) ? 0 : 1;
    }

    private static async Task<List<SendResult>> SendAsync(
        IKSeFClient ksef, ICryptographyService crypto, AccessTokenProvider tokens,
        List<(string Name, byte[] Xml)> invoices, CancellationToken cancellationToken)
    {
        EncryptionData encryption = crypto.GetEncryptionData();
        OpenOnlineSessionRequest open = OpenOnlineSessionRequestBuilder.Create()
            .WithFormCode(
                systemCode: SystemCodeHelper.GetSystemCode(SystemCode.FA3),
                schemaVersion: SystemCodeHelper.GetSchemaVersion(SystemCode.FA3),
                value: SystemCodeHelper.GetValue(SystemCode.FA3))
            .WithEncryption(
                encryptedSymmetricKey: encryption.EncryptionInfo.EncryptedSymmetricKey,
                initializationVector: encryption.EncryptionInfo.InitializationVector,
                publicKeyId: encryption.EncryptionInfo.PublicKeyId)
            .Build();
        OpenOnlineSessionResponse online = await ksef.OpenOnlineSessionAsync(open, await tokens.GetAsync(cancellationToken), cancellationToken: cancellationToken);
        string sessionReference = online.ReferenceNumber;

        List<SendResult> results = [];
        try
        {
            List<(int Index, string Name, string Reference)> sent = [];
            for (int index = 0; index < invoices.Count; index++)
            {
                (string name, byte[] xml) = invoices[index];
                byte[] encrypted = crypto.EncryptBytesWithAES256(xml, encryption.CipherKey, encryption.CipherIv);
                FileMetadata plain = crypto.GetMetaData(xml);
                FileMetadata cipher = crypto.GetMetaData(encrypted);
                SendInvoiceRequest request = SendInvoiceOnlineSessionRequestBuilder.Create()
                    .WithInvoiceHash(plain.HashSHA, plain.FileSize)
                    .WithEncryptedDocumentHash(cipher.HashSHA, cipher.FileSize)
                    .WithEncryptedDocumentContent(Convert.ToBase64String(encrypted))
                    .Build();
                try
                {
                    SendInvoiceResponse response = await ksef.SendOnlineSessionInvoiceAsync(request, sessionReference, await tokens.GetAsync(cancellationToken), cancellationToken);
                    sent.Add((index, name, response.ReferenceNumber));
                }
                catch (KsefApiException ex)
                {
                    results.Add(new SendResult(index, name, sessionReference, null, null, (int)ex.StatusCode, ex.Message, null, null));
                }
            }

            foreach ((int index, string name, string reference) in sent)
            {
                SessionInvoice status = await WaitForStatusAsync(ksef, tokens, sessionReference, reference, cancellationToken);
                results.Add(new SendResult(
                    index, name, sessionReference, reference,
                    status.Status.Code == Accepted ? status.KsefNumber : null,
                    status.Status.Code, status.Status.Description, [.. status.Status.Details ?? []], status.Status.Extensions));
            }
        }
        finally
        {
            // Zamknięcie sesji uruchamia generowanie UPO.
            await ksef.CloseOnlineSessionAsync(sessionReference, await tokens.GetAsync(CancellationToken.None), CancellationToken.None);
        }

        // Kolejność wyników jak kolejność plików na wejściu.
        return [.. results.OrderBy(r => r.Index)];
    }

    private static async Task<SessionInvoice> WaitForStatusAsync(
        IKSeFClient ksef, AccessTokenProvider tokens, string sessionReference, string invoiceReference, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + PollTimeout;
        while (true)
        {
            SessionInvoice status = await ksef.GetSessionInvoiceAsync(sessionReference, invoiceReference, await tokens.GetAsync(cancellationToken), cancellationToken);
            if (!InProgress.Contains(status.Status.Code) || DateTime.UtcNow >= deadline)
            {
                return status;
            }
            await Task.Delay(PollInterval, cancellationToken);
        }
    }
}
