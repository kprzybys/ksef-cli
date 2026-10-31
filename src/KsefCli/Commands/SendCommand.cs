using System.CommandLine;
using System.Security.Cryptography;
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

    // „Sesja tymczasowo niedostępna” (API 2.8.0+): faktury nie przyjęto, MF zaleca nową sesję i ponowienie.
    private const int SessionTemporarilyUnavailable = 21184;
    private const int MaxSessionReopens = 3;

    // Index: pozycja pliku na wejściu — ten sam plik może wystąpić dwa razy.
    // internal: typ wyniku --json musi być widoczny dla CliJsonContext.
    internal sealed record SendResult(
        int Index, string File, string SessionReferenceNumber, string? InvoiceReferenceNumber, string? KsefNumber,
        int? Code, int? HttpStatus, string? Description, IReadOnlyCollection<string>? Details, IDictionary<string, string>? Extensions)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public int Index { get; init; } = Index;

        public bool Accepted => KsefNumber is not null;

        /// <summary>true: nie wiadomo, czy KSeF fakturę przyjął — sprawdzić przed ponowną wysyłką (w JSON tylko wtedy).</summary>
        public bool? StatusUnknown { get; init; }
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

    private static string InvoicesWord(int count) => count switch
    {
        1 => "fakturę",
        _ when count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14 => "faktury",
        _ => "faktur",
    };

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
            IReadOnlyList<Fa3ValidationError> errors = Fa3Validator.Validate(xml);
            foreach (Fa3ValidationError error in errors)
            {
                string severity = error.Severity == System.Xml.Schema.XmlSeverityType.Error ? "błąd" : "ostrzeżenie";
                Console.Error.WriteLine($"{name}:{error.Line}:{error.Column}: {error.Source} {severity}: {error.Message}");
                invalid = true;
            }
            if (errors.Count == 0 && InvoiceQr.SellerAndIssueDate(xml).IssueDate is { } issued && issued > PolandToday())
            {
                // KSeF odrzuca fakturę z datą wystawienia późniejszą niż dzień jej przyjęcia.
                CliIo.Error($"{name}: data wystawienia P_1 {issued:yyyy-MM-dd} jest w przyszłości — KSeF ją odrzuci.");
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
        bool interrupted;
        try
        {
            KsefSession session = SessionStore.Resolve(env.Name, nip);
            if (env.Name == KsefEnvironment.Prod.Name)
            {
                // Jedna linia na stderr, nic nie blokuje: ślad w logu crona, że faktury poszły naprawdę.
                CliIo.Error($"PRODUKCJA, NIP {session.Nip}: wysyłam {invoices.Count} {InvoicesWord(invoices.Count)} — wysłana = wystawiona.");
            }
            await using ServiceProvider provider = await KsefServices.CreateWithCryptographyAsync(env, cancellationToken);
            using IServiceScope scope = provider.CreateScope();
            AccessTokenProvider tokens = new(session, scope.ServiceProvider.GetRequiredService<IAuthorizationClient>());
            (results, interrupted) = await SendAsync(
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
            string code = result.StatusUnknown is true ? "STATUS NIEZNANY: "
                : result.Code is { } c ? $"KSeF {c} " : result.HttpStatus is { } h ? $"HTTP {h} " : "";
            string reference = result.StatusUnknown is true && result.InvoiceReferenceNumber is { } r ? $" (numer referencyjny {r}, sesja {result.SessionReferenceNumber})" : "";
            string details = result.Details is { Count: > 0 } d ? $" ({string.Join("; ", d)})" : "";
            string extensions = result.Extensions is { Count: > 0 } e ? $" [{string.Join(", ", e.Select(kv => $"{kv.Key}={kv.Value}"))}]" : "";
            CliIo.Error($"{result.File}: {code}{result.Description}{reference}{details}{extensions}");
        }
        return interrupted ? 130 : results.All(r => r.Accepted) ? 0 : 1;
    }

    /// <summary>Wyniki w kolejności wejścia; Interrupted — przerwano (Ctrl+C, SIGTERM), części faktur nie sprawdzono.</summary>
    private sealed record SendOutcome(List<SendResult> Results, bool Interrupted);

    private static async Task<SendOutcome> SendAsync(
        IKSeFClient ksef, ICryptographyService crypto, AccessTokenProvider tokens,
        List<(string Name, byte[] Xml)> invoices, CancellationToken cancellationToken)
    {
        OnlineSession session = await OpenSessionAsync(ksef, crypto, tokens, cancellationToken);
        HashSet<string> used = new(StringComparer.Ordinal);
        int reopens = 0;

        // Błąd przy jednej fakturze (KSeF, sieć, limit czasu) dotyczy tylko jej: numery KSeF już przyjętych
        // faktur muszą trafić na wyjście, bo ponowna wysyłka skończy się duplikatem (440).
        List<SendResult> results = [];
        List<(int Index, string Name, string Session, string Reference)> sent = [];
        // Odpowiedź na wysyłkę nie dotarła (sieć, limit czasu, błąd bramy) — KSeF mógł fakturę przyjąć.
        List<(int Index, string Name, string Session, byte[] Xml, Exception Error, string Message)> unknown = [];
        // Powód, dla którego kolejnych faktur już nie wysyłamy (nie da się otworzyć sesji, przerwanie).
        string? stop = null;
        bool interrupted = false;
        for (int index = 0; index < invoices.Count; index++)
        {
            (string name, byte[] xml) = invoices[index];
            if (stop is not null)
            {
                results.Add(NotSent(index, name, session.Reference, stop));
                continue;
            }
            while (true)
            {
                try
                {
                    SendInvoiceResponse response = await KsefRetry.OnRateLimitAsync(async () =>
                        await ksef.SendOnlineSessionInvoiceAsync(InvoiceRequest(crypto, session.Encryption, xml), session.Reference,
                            await tokens.GetAsync(cancellationToken), cancellationToken), cancellationToken);
                    sent.Add((index, name, session.Reference, response.ReferenceNumber));
                    used.Add(session.Reference);
                    break;
                }
                catch (KsefApiException ex) when (KsefCode(ex) == SessionTemporarilyUnavailable && reopens < MaxSessionReopens)
                {
                    // Ta faktura i następne idą w nowej sesji; każdy wynik ma swój numer sesji (do UPO).
                    reopens++;
                    CliIo.Error($"sesja {session.Reference} tymczasowo niedostępna (KSeF {SessionTemporarilyUnavailable}) — wysyłam dalej w nowej sesji.");
                    try
                    {
                        session = await OpenSessionAsync(ksef, crypto, tokens, cancellationToken);
                    }
                    catch (Exception open) when (!cancellationToken.IsCancellationRequested && CliErrors.Describe(open) is { } message)
                    {
                        // Bez sesji nie wyślemy nic więcej, ale już przyjęte faktury dalej sprawdzamy i podajemy.
                        stop = $"nie wysłano — nie udało się otworzyć nowej sesji: {message}";
                        results.Add(NotSent(index, name, session.Reference, stop));
                        break;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // KSeF tej faktury nie przyjął (21184), a nowej sesji już nie ma.
                        interrupted = true;
                        stop = "nie wysłano — przerwano";
                        results.Add(NotSent(index, name, session.Reference, stop));
                        break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Przerwane w trakcie wysyłki tej faktury: mogła dotrzeć do KSeF.
                    interrupted = true;
                    stop = "nie wysłano — przerwano";
                    results.Add(Uncertain(index, name, session.Reference, null,
                        "przerwano w trakcie wysyłki — faktura mogła trafić do KSeF; sprawdź przed ponowną wysyłką"));
                    break;
                }
                catch (Exception ex) when (IsResponseLost(ex) && CliErrors.Describe(ex) is { } message)
                {
                    unknown.Add((index, name, session.Reference, xml, ex, message));
                    break;
                }
                catch (Exception ex) when (CliErrors.Describe(ex) is { } message)
                {
                    results.Add(Failed(index, name, session.Reference, null, ex, message));
                    break;
                }
            }
        }

        if (interrupted)
        {
            // Po przerwaniu nie czekamy na statusy — ale numery referencyjne wysłanych faktur muszą wyjść,
            // bo faktury mogą być już wystawione.
            foreach ((int index, string name, string sessionReference, string reference) in sent)
            {
                results.Add(Uncertain(index, name, sessionReference, reference, "przerwano przed numerem KSeF — faktura jest w KSeF; sprawdź jej status"));
            }
            foreach ((int index, string name, string sessionReference, _, _, string message) in unknown)
            {
                results.Add(Uncertain(index, name, sessionReference, null, $"przerwano; odpowiedź KSeF nie dotarła ({message})"));
            }
        }
        else
        {
            await ResolveUnknownAsync(ksef, tokens, unknown, sent, used, results, cancellationToken);
            await AddStatusesAsync(ksef, tokens, sent, results, cancellationToken);
        }

        // Zamknięcie sesji uruchamia generowanie UPO. Sesję bez wysłanych faktur KSeF zamyka sam.
        // Własny limit czasu: po przerwaniu proces ma tylko chwilę na zakończenie.
        using CancellationTokenSource closing = new(interrupted ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(1));
        foreach (string sessionReference in used)
        {
            try
            {
                await ksef.CloseOnlineSessionAsync(sessionReference, await tokens.GetAsync(closing.Token), closing.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException || CliErrors.Describe(ex) is not null)
            {
                // KSeF zamyka nieużywaną sesję sam; wyniki faktur są ważne niezależnie od tego.
                string message = CliErrors.Describe(ex) ?? "przekroczono czas";
                CliIo.Error($"sesja {sessionReference}: nie udało się jej zamknąć ({message}) — UPO pojawi się po jej wygaśnięciu.");
            }
        }

        // Kolejność wyników jak kolejność plików na wejściu.
        return new SendOutcome([.. results.OrderBy(r => r.Index)], interrupted || cancellationToken.IsCancellationRequested);
    }

    // Zamiast zgadywać: faktura jest w sesji, jeśli KSeF zna jej skrót. Inaczej ponowna wysyłka po zmianie
    // numeru faktury (P_2) dałaby duplikat, którego KSeF już nie wykryje.
    private static async Task ResolveUnknownAsync(
        IKSeFClient ksef, AccessTokenProvider tokens,
        List<(int Index, string Name, string Session, byte[] Xml, Exception Error, string Message)> unknown,
        List<(int Index, string Name, string Session, string Reference)> sent, HashSet<string> used,
        List<SendResult> results, CancellationToken cancellationToken)
    {
        foreach (var group in unknown.GroupBy(u => u.Session, StringComparer.Ordinal))
        {
            Dictionary<string, string> inSession;
            try
            {
                inSession = await InvoicesInSessionAsync(ksef, tokens, group.Key, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                inSession = [];
            }
            foreach ((int index, string name, string sessionReference, byte[] xml, _, string message) in group)
            {
                if (inSession.TryGetValue(Convert.ToBase64String(SHA256.HashData(xml)), out string? reference))
                {
                    CliIo.Error($"{name}: odpowiedź KSeF na wysyłkę nie dotarła, ale faktura jest w sesji {sessionReference} — czekam na jej status.");
                    sent.Add((index, name, sessionReference, reference));
                    used.Add(sessionReference);
                }
                else
                {
                    // Nie widać jej w sesji, ale lista sesji bywa opóźniona — to nie jest pewne odrzucenie.
                    used.Add(sessionReference);
                    results.Add(Uncertain(index, name, sessionReference, null,
                        $"odpowiedź KSeF nie dotarła ({message}) i faktury nie widać w sesji — mogła zostać przyjęta; "
                        + "przed ponowną wysyłką sprawdź: ksef list --subject seller --date-type invoicing"));
                }
            }
        }
    }

    private static async Task AddStatusesAsync(
        IKSeFClient ksef, AccessTokenProvider tokens, List<(int Index, string Name, string Session, string Reference)> sent,
        List<SendResult> results, CancellationToken cancellationToken)
    {
        foreach ((int index, string name, string sessionReference, string reference) in sent)
        {
            try
            {
                SessionInvoice status = await WaitForStatusAsync(ksef, tokens, sessionReference, reference, cancellationToken);
                results.Add(new SendResult(
                    index, name, sessionReference, reference,
                    status.Status.Code == Accepted ? status.KsefNumber : null,
                    status.Status.Code, null, status.Status.Description, [.. status.Status.Details ?? []], status.Status.Extensions));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                results.Add(Uncertain(index, name, sessionReference, reference, "przerwano przed numerem KSeF — faktura jest w KSeF; sprawdź jej status"));
            }
            catch (Exception ex) when (CliErrors.Describe(ex) is { } message)
            {
                // Faktura wysłana, ale statusu nie znamy — numer referencyjny pozwala sprawdzić ją później.
                results.Add(Uncertain(index, name, sessionReference, reference, $"brak statusu faktury {reference}: {message}"));
            }
        }
    }

    // Dzień przyjęcia faktury liczy KSeF, czyli czas polski — niezależnie od strefy tej maszyny.
    private static DateTime PolandToday()
    {
        TimeZoneInfo zone = TimeZoneInfo.TryFindSystemTimeZoneById("Europe/Warsaw", out TimeZoneInfo? warsaw) ? warsaw : TimeZoneInfo.Local;
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone).Date;
    }

    /// <summary>Skrót SHA-256 (Base64) → numer referencyjny faktur, które KSeF zarejestrował w sesji; przy błędzie pusto.</summary>
    private static async Task<Dictionary<string, string>> InvoicesInSessionAsync(
        IKSeFClient ksef, AccessTokenProvider tokens, string sessionReference, CancellationToken cancellationToken)
    {
        Dictionary<string, string> invoices = new(StringComparer.Ordinal);
        // Lista sesji bywa chwilę opóźniona względem wysyłki.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                string? continuation = null;
                do
                {
                    SessionInvoicesResponse page = await KsefRetry.OnRateLimitAsync(async () =>
                        await ksef.GetSessionInvoicesAsync(sessionReference, await tokens.GetAsync(cancellationToken),
                            continuationToken: continuation, cancellationToken: cancellationToken), cancellationToken);
                    foreach (SessionInvoice invoice in page.Invoices ?? [])
                    {
                        if (invoice.InvoiceHash is { } hash && invoice.ReferenceNumber is { } reference)
                        {
                            invoices[hash] = reference;
                        }
                    }
                    continuation = page.ContinuationToken;
                }
                while (!string.IsNullOrEmpty(continuation));
                return invoices;
            }
            catch (Exception ex) when (CliErrors.Describe(ex) is not null)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
        return invoices;
    }

    private sealed record OnlineSession(string Reference, EncryptionData Encryption);

    private static async Task<OnlineSession> OpenSessionAsync(
        IKSeFClient ksef, ICryptographyService crypto, AccessTokenProvider tokens, CancellationToken cancellationToken)
    {
        // Nowy klucz AES na każdą sesję.
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
        return new OnlineSession(online.ReferenceNumber, encryption);
    }

    private static SendInvoiceRequest InvoiceRequest(ICryptographyService crypto, EncryptionData encryption, byte[] xml)
    {
        byte[] encrypted = crypto.EncryptBytesWithAES256(xml, encryption.CipherKey, encryption.CipherIv);
        FileMetadata plain = crypto.GetMetaData(xml);
        FileMetadata cipher = crypto.GetMetaData(encrypted);
        return SendInvoiceOnlineSessionRequestBuilder.Create()
            .WithInvoiceHash(plain.HashSHA, plain.FileSize)
            .WithEncryptedDocumentHash(cipher.HashSHA, cipher.FileSize)
            .WithEncryptedDocumentContent(Convert.ToBase64String(encrypted))
            .Build();
    }

    // Sieć, limit czasu, urwana odpowiedź 2xx (biblioteka MF zgłasza ją jako KsefApiException bez kodu KSeF)
    // albo 408/5xx bez kodu KSeF (np. 504 od bramy, gdy KSeF jeszcze przetwarzał): nie wiadomo, czy fakturę przyjął.
    // Pewne odrzucenie to tylko błąd z kodem KSeF albo inny 4xx.
    private static bool IsResponseLost(Exception ex) =>
        ex is not KsefApiException api
        || (KsefCode(api) is null && api.StatusCode is (>= System.Net.HttpStatusCode.OK and < System.Net.HttpStatusCode.MultipleChoices)
            or System.Net.HttpStatusCode.RequestTimeout or >= System.Net.HttpStatusCode.InternalServerError);

    private static int? KsefCode(KsefApiException? ex) => ex?.ErrorResponse?.Exception?.ExceptionDetailList?.FirstOrDefault()?.ExceptionCode;

    /// <summary>Faktury nie wysłano wcale (sesji nie da się otworzyć, przerwano przed nią) — można ją wysłać ponownie.</summary>
    private static SendResult NotSent(int index, string name, string sessionReference, string reason) =>
        new(index, name, sessionReference, null, null, null, null, reason, null, null);

    /// <summary>Nie wiadomo, czy KSeF fakturę przyjął — ponowna wysyłka może dać duplikat; przed nią trzeba sprawdzić.</summary>
    private static SendResult Uncertain(int index, string name, string sessionReference, string? invoiceReference, string reason) =>
        new(index, name, sessionReference, invoiceReference, null, null, null, reason, null, null) { StatusUnknown = true };

    /// <summary>Wynik dla faktury, której nie przyjęto przez błąd żądania: kod KSeF z treści błędu, osobno status HTTP.</summary>
    private static SendResult Failed(int index, string name, string sessionReference, string? invoiceReference, Exception ex, string message)
    {
        KsefApiException? api = ex as KsefApiException;
        int? code = KsefCode(api);
        // Biblioteka MF zgłasza też nieczytelną odpowiedź 2xx (np. urwaną) — to nie jest błąd HTTP.
        int? httpStatus = api is { StatusCode: < System.Net.HttpStatusCode.OK or >= System.Net.HttpStatusCode.MultipleChoices } ? (int)api.StatusCode : null;
        return new SendResult(index, name, sessionReference, invoiceReference, null, code, httpStatus, message, null, null);
    }

    private static async Task<SessionInvoice> WaitForStatusAsync(
        IKSeFClient ksef, AccessTokenProvider tokens, string sessionReference, string invoiceReference, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + PollTimeout;
        while (true)
        {
            SessionInvoice status = await KsefRetry.OnRateLimitAsync(async () =>
                await ksef.GetSessionInvoiceAsync(sessionReference, invoiceReference, await tokens.GetAsync(cancellationToken), cancellationToken), cancellationToken);
            if (!InProgress.Contains(status.Status.Code) || DateTime.UtcNow >= deadline)
            {
                return status;
            }
            await Task.Delay(PollInterval, cancellationToken);
        }
    }
}
