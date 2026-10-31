using System.CommandLine;
using System.Globalization;
using System.Text;
using KSeF.Client.Core.Exceptions;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Models.Sessions;
using KSeF.Client.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace KsefCli.Commands;

/// <summary>
/// `ksef upo` — Urzędowe Poświadczenie Odbioru: dla faktur (numer KSeF + numer sesji, w której je wysłano)
/// albo zbiorcze dla całej sesji (`--collective`). Wejście w formacie wyniku `ksef send`
/// (numer KSeF, plik, numer sesji), więc `ksef send *.xml | ksef upo -d upo/` działa wprost.
/// UPO faktury KSeF wystawia razem z numerem KSeF — także przy wciąż otwartej sesji (np. innego programu).
/// Zbiorcze UPO powstaje asynchronicznie dopiero po zamknięciu sesji (ręcznym albo po 12 h) — na nie czekamy.
/// </summary>
public static partial class UpoCommand
{
    // Kod statusu sesji: 100 otwarta, 170 zamknięta (zbiorcze UPO w drodze), 200 przetworzona (zbiorcze UPO gotowe).
    private const int SessionOpen = 100;
    private const int SessionClosed = 170;
    private const int SessionProcessed = 200;

    // KSeF 21178 „Nie znaleziono UPO”: ten sam kod, gdy faktura jest jeszcze w przetwarzaniu i gdy jej w sesji
    // nie ma — rozstrzygają liczniki faktur w statusie sesji.
    private const int UpoNotFound = 21178;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InvoicePollTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SessionPollTimeout = TimeSpan.FromMinutes(3);

    public sealed record UpoRequest(string? KsefNumber, string SessionReferenceNumber);

    public static Command Create(Option<string> envOption)
    {
        Argument<string[]> numbersArgument = new("numery")
        {
            Description = "Numery KSeF; `-` albo brak = stdin w formacie `ksef send` (numer KSeF, plik, numer sesji)",
            Arity = ArgumentArity.ZeroOrMore,
        };
        Option<string> sessionOption = new("--session")
        {
            Description = "Numer referencyjny sesji wysyłki (dla numerów bez kolumny sesji, i dla --collective)",
        };
        Option<bool> collectiveOption = new("--collective")
        {
            Description = "Zbiorcze UPO całej sesji z --session zamiast UPO faktur",
        };
        Option<string> dirOption = new("--dir", "-d")
        {
            Description = "Katalog na pliki <numer KSeF>.upo.xml albo <numer sesji>.upo.xml; wymagany przy kilku UPO",
        };
        Option<string> nipOption = Nip.Option("NIP sesji logowania (wymagany, gdy w środowisku jest kilka sesji)");

        Command command = new("upo", "Pobiera UPO faktur (po numerze KSeF i sesji wysyłki) albo zbiorcze UPO sesji. Kod wyjścia: 0 wszystkie pobrane, 1 jest błąd");
        command.Arguments.Add(numbersArgument);
        command.Options.Add(sessionOption);
        command.Options.Add(collectiveOption);
        command.Options.Add(dirOption);
        command.Options.Add(nipOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            string? session = parseResult.GetValue(sessionOption);
            string[] numbers = parseResult.GetValue(numbersArgument) ?? [];
            IReadOnlyList<UpoRequest> requests;
            if (parseResult.GetValue(collectiveOption))
            {
                if (session is null || numbers.Length > 0)
                {
                    CliIo.Error("--collective wymaga --session i nie przyjmuje numerów KSeF.");
                    return 1;
                }
                requests = [new UpoRequest(null, session)];
            }
            else
            {
                try
                {
                    requests = ParseRequests(numbers, session, numbers is [] or [CliIo.StdStream] ? ReadLines() : []);
                }
                catch (FormatException ex)
                {
                    CliIo.Error(ex.Message);
                    return 1;
                }
            }
            return await RunAsync(
                KsefEnvironment.Parse(parseResult.GetRequiredValue(envOption)),
                parseResult.GetValue(nipOption),
                requests,
                parseResult.GetValue(dirOption),
                cancellationToken);
        });
        return command;
    }

    private static List<string> ReadLines()
    {
        List<string> lines = [];
        while (Console.In.ReadLine() is { } line)
        {
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>
    /// Numery z argumentów (sesja z --session) albo wiersze stdin: pierwsza kolumna to numer KSeF,
    /// trzecia — jeśli wygląda na numer sesji (format `ksef send`) — sesja, inaczej --session.
    /// W `ksef list` trzecia kolumna to data wystawienia, więc taki wiersz też bierze --session.
    /// </summary>
    public static IReadOnlyList<UpoRequest> ParseRequests(string[] numbers, string? defaultSession, IReadOnlyList<string> stdinLines)
    {
        IEnumerable<(string Number, string? Session)> items = numbers is [] or [CliIo.StdStream]
            ? stdinLines
                .Select(line => line.Split('\t'))
                .Where(columns => columns[0].Trim().Length > 0)
                .Select(columns => (columns[0].Trim(), columns.Length >= 3 && RegexPatterns.ReferenceNumber.IsMatch(columns[2].Trim()) ? columns[2].Trim() : defaultSession))
            : numbers.Select(n => (n, defaultSession));

        List<UpoRequest> requests = [];
        foreach ((string number, string? session) in items)
        {
            if (!RegexPatterns.KsefNumber.IsMatch(number))
            {
                throw new FormatException($"niepoprawny numer KSeF: {number}");
            }
            if (session is null)
            {
                throw new FormatException($"{number}: brak numeru sesji — podaj --session albo wiersze z `ksef send`.");
            }
            requests.Add(new UpoRequest(number, session));
        }
        if (requests.Count == 0)
        {
            throw new FormatException("brak numerów KSeF.");
        }
        return [.. requests.Distinct()];
    }

    private static async Task<int> RunAsync(KsefEnvironment env, string? nip, IReadOnlyList<UpoRequest> requests, string? dir, CancellationToken cancellationToken)
    {
        if (requests.FirstOrDefault(r => !RegexPatterns.ReferenceNumber.IsMatch(r.SessionReferenceNumber)) is { } invalid)
        {
            CliIo.Error($"niepoprawny numer sesji: {invalid.SessionReferenceNumber}");
            return 1;
        }
        if (dir is null && requests.Count > 1)
        {
            CliIo.Error("kilka UPO naraz zapisuję tylko do katalogu — podaj -d <katalog>.");
            return 1;
        }

        bool failed = false;
        try
        {
            KsefSession login = SessionStore.Resolve(env.Name, nip);
            await using ServiceProvider provider = KsefServices.Create(env);
            using IServiceScope scope = provider.CreateScope();
            AccessTokenProvider tokens = new(login, scope.ServiceProvider.GetRequiredService<IAuthorizationClient>());
            IKSeFClient ksef = scope.ServiceProvider.GetRequiredService<IKSeFClient>();
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            foreach (UpoRequest request in requests)
            {
                string label = request.KsefNumber ?? request.SessionReferenceNumber;
                try
                {
                    List<(string Name, string Xml)> documents = [];
                    if (request.KsefNumber is { } number)
                    {
                        if (await FetchInvoiceUpoAsync(ksef, tokens, number, request.SessionReferenceNumber, cancellationToken) is not { } upo)
                        {
                            failed = true;
                            continue;
                        }
                        documents.Add(($"{number}.upo.xml", upo));
                    }
                    else
                    {
                        if (await WaitForSessionUpoAsync(ksef, tokens, request.SessionReferenceNumber, cancellationToken) is not { } status)
                        {
                            failed = true;
                            continue;
                        }
                        // Zbiorcze UPO dużej sesji bywa podzielone na strony.
                        List<UpoPageResponse> pages = [.. status.Upo?.Pages ?? []];
                        if (pages.Count == 0)
                        {
                            CliIo.Error($"{label}: sesja przetworzona, ale KSeF nie wystawił dla niej UPO (np. wszystkie faktury odrzucone).");
                            failed = true;
                            continue;
                        }
                        if (pages.FirstOrDefault(p => !SafeName().IsMatch(p.ReferenceNumber ?? "")) is { } odd)
                        {
                            CliIo.Error($"{label}: nieoczekiwany numer strony UPO: {odd.ReferenceNumber}");
                            failed = true;
                            continue;
                        }
                        foreach (UpoPageResponse page in pages)
                        {
                            string name = pages.Count == 1 ? $"{request.SessionReferenceNumber}.upo.xml" : $"{request.SessionReferenceNumber}-{page.ReferenceNumber}.upo.xml";
                            documents.Add((name, await KsefRetry.OnRateLimitAsync(async () => await ksef.GetSessionUpoAsync(
                                request.SessionReferenceNumber, page.ReferenceNumber, await tokens.GetAsync(cancellationToken), cancellationToken), cancellationToken)));
                        }
                        if (dir is null && documents.Count > 1)
                        {
                            CliIo.Error($"{label}: UPO sesji ma {documents.Count} stron — podaj -d <katalog>.");
                            failed = true;
                            continue;
                        }
                    }

                    foreach ((string name, string xml) in documents)
                    {
                        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(xml);
                        if (dir is null)
                        {
                            CliIo.WriteAllBytes(CliIo.StdStream, bytes);
                        }
                        else
                        {
                            string path = Path.Combine(dir, name);
                            CliIo.WriteFileAtomically(path, bytes);
                            Console.Out.WriteLine(path);
                        }
                    }
                }
                catch (KsefApiException ex)
                {
                    CliIo.Error($"{label}: {ex.Message}");
                    failed = true;
                }
            }
        }
        catch (Exception ex) when (ex is SessionException or KsefApiException or HttpRequestException)
        {
            CliIo.Error(ex.Message);
            return 1;
        }
        return failed ? 1 : 0;
    }

    /// <summary>
    /// UPO faktury: najpierw po prostu pobieramy. Przy „nie znaleziono UPO” czekamy tylko wtedy, gdy sesja ma jeszcze
    /// faktury w przetwarzaniu; inaczej od razu błąd. Null (z komunikatem), gdy UPO nie ma.
    /// </summary>
    private static async Task<string?> FetchInvoiceUpoAsync(
        IKSeFClient ksef, AccessTokenProvider tokens, string ksefNumber, string sessionReference, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + InvoicePollTimeout;
        while (true)
        {
            try
            {
                return await KsefRetry.OnRateLimitAsync(async () => await ksef.GetSessionInvoiceUpoByKsefNumberAsync(
                    sessionReference, ksefNumber, await tokens.GetAsync(cancellationToken), cancellationToken), cancellationToken);
            }
            catch (KsefApiException ex) when (KsefErrorCode(ex) == UpoNotFound)
            {
                SessionStatusResponse status = await GetSessionStatusAsync(ksef, tokens, sessionReference, cancellationToken);
                if (status.Status.Code is not (SessionOpen or SessionClosed or SessionProcessed))
                {
                    CliIo.Error($"sesja {sessionReference}: KSeF {status.Status.Code} {status.Status.Description}");
                    return null;
                }
                int pending = (status.InvoiceCount ?? 0) - (status.SuccessfulInvoiceCount ?? 0) - (status.FailedInvoiceCount ?? 0);
                if (pending <= 0)
                {
                    CliIo.Error($"{ksefNumber}: KSeF nie ma UPO tej faktury w sesji {sessionReference} (faktura odrzucona albo wysłana w innej sesji): {ex.Message}");
                    return null;
                }
                if (DateTime.UtcNow >= deadline)
                {
                    CliIo.Error($"{ksefNumber}: UPO niegotowe po {InvoicePollTimeout.TotalMinutes:0} min "
                        + $"({pending} faktur sesji {sessionReference} w przetwarzaniu) — spróbuj później.");
                    return null;
                }
                await Task.Delay(PollInterval, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Zbiorcze UPO: status sesji z gotowymi stronami; null (z komunikatem), gdy sesja otwarta, odrzucona albo UPO
    /// się nie doczekało. Otwartej sesji nie zamykamy — mógł ją otworzyć inny program, który jeszcze wysyła.
    /// </summary>
    private static async Task<SessionStatusResponse?> WaitForSessionUpoAsync(IKSeFClient ksef, AccessTokenProvider tokens, string sessionReference, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + SessionPollTimeout;
        while (true)
        {
            SessionStatusResponse status = await GetSessionStatusAsync(ksef, tokens, sessionReference, cancellationToken);
            switch (status.Status.Code)
            {
                case SessionProcessed:
                    return status;
                case SessionOpen:
                    string until = status.ValidUntil is { } validUntil
                        ? $" (wygaśnie {validUntil.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)})"
                        : "";
                    CliIo.Error($"sesja {sessionReference}: wciąż otwarta{until} — zbiorcze UPO powstaje po zamknięciu sesji przez program, "
                        + "który ją otworzył, albo po jej wygaśnięciu. UPO pojedynczych faktur są dostępne już teraz (bez --collective).");
                    return null;
                case SessionClosed when DateTime.UtcNow < deadline:
                    await Task.Delay(PollInterval, cancellationToken);
                    break;
                case SessionClosed:
                    CliIo.Error($"sesja {sessionReference}: zbiorcze UPO niegotowe po {SessionPollTimeout.TotalMinutes:0} min "
                        + $"(status {status.Status.Code} {status.Status.Description}) — spróbuj później.");
                    return null;
                default:
                    CliIo.Error($"sesja {sessionReference}: KSeF {status.Status.Code} {status.Status.Description}");
                    return null;
            }
        }
    }

    private static Task<SessionStatusResponse> GetSessionStatusAsync(IKSeFClient ksef, AccessTokenProvider tokens, string sessionReference, CancellationToken cancellationToken) =>
        KsefRetry.OnRateLimitAsync(async () =>
            await ksef.GetSessionStatusAsync(sessionReference, await tokens.GetAsync(cancellationToken), cancellationToken), cancellationToken);

    private static int? KsefErrorCode(KsefApiException ex) =>
        ex.ErrorResponse?.Exception?.ExceptionDetailList?.FirstOrDefault()?.ExceptionCode;

    // Numer strony UPO z odpowiedzi KSeF trafia do nazwy pliku — tylko bezpieczne znaki.
    [System.Text.RegularExpressions.GeneratedRegex("^[0-9A-Za-z-]{1,100}$", System.Text.RegularExpressions.RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial System.Text.RegularExpressions.Regex SafeName();
}
