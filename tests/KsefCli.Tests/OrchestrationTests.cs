using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KsefCli.Tests;

/// <summary>
/// Przebiegi `send`, `get` i `upo` bez sieci: `ksef` jako proces przeciw <see cref="FakeKsef"/>.
/// Sprawdzają to, czego żywy KSeF TEST nie da wywołać na żądanie: zerwane połączenie w połowie wysyłki,
/// odrzucenia, limity żądań, faktury jeszcze niedostępne, sesje bez UPO.
/// </summary>
public class SendOrchestrationTests
{
    private const string Nip = "5908996669";
    private const string Session = "20261030-SO-394363C000-F9191A9DA9-98";

    private static string InvoiceReference(int n) => $"20261030-EE-{n:D10}-0000000000-00";

    private static string KsefNumber(int n) => $"{Nip}-20261030-{n:X12}-00";

    private static FakeResponse InvoiceStatus(int code, string description, string? ksefNumber = null, string[]? details = null, Dictionary<string, string>? extensions = null) =>
        FakeResponse.Json(new
        {
            ordinalNumber = 1,
            referenceNumber = "x",
            invoicingDate = DateTimeOffset.UtcNow,
            ksefNumber,
            status = new { code, description, details, extensions },
        });

    /// <summary>
    /// Sesja interaktywna: n-ta wysłana faktura dostaje numer referencyjny n; `invoice` decyduje o odpowiedzi
    /// na wysyłkę (null = przyjęta), `status` o statusie faktury (domyślnie 200 z numerem KSeF n).
    /// </summary>
    private static FakeKsef OnlineSession(Func<int, FakeResponse?>? invoice = null, Func<int, FakeResponse>? status = null)
    {
        FakeKsef ksef = new();
        int sent = 0;
        ksef.On("POST", "sessions/online", (_, _) => FakeResponse.Json(new { referenceNumber = Session, validUntil = DateTimeOffset.UtcNow.AddHours(12) }));
        ksef.On("POST", $"sessions/online/{Session}/invoices", (_, _) =>
        {
            int n = Interlocked.Increment(ref sent);
            return invoice?.Invoke(n) ?? FakeResponse.Json(new { referenceNumber = InvoiceReference(n) });
        });
        ksef.On("GET", $@"sessions/{Session}/invoices/20261030-EE-(\d{{10}})-0000000000-00", (_, match) =>
        {
            int n = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            return status?.Invoke(n) ?? InvoiceStatus(200, "Sukces", KsefNumber(n));
        });
        ksef.On("POST", $"sessions/online/{Session}/close", (_, _) => new FakeResponse(204));
        return ksef;
    }

    private static (TempDir Dir, string[] Files) Invoices(int count)
    {
        TempDir dir = new();
        string[] files = [.. Enumerable.Range(1, count).Select(i => dir.File($"f{i}.xml"))];
        foreach (string file in files)
        {
            File.WriteAllBytes(file, TestData.InvoiceToSend(Nip));
        }
        FakeKsef.WriteLoginSession(dir.Path, Nip);
        return (dir, files);
    }

    [Fact]
    public async Task Send_EncryptsExactFileBytes_PrintsNumbersInInputOrder_ClosesSession()
    {
        using FakeKsef ksef = OnlineSession();
        (TempDir dir, string[] files) = Invoices(2);
        using TempDir _ = dir;

        CliResult result = await ksef.RunAsync(dir.Path, ["send", .. files]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stderr);
        Assert.Equal($"{KsefNumber(1)}\t{files[0]}\t{Session}\n{KsefNumber(2)}\t{files[1]}\t{Session}\n", result.Stdout);

        // KSeF dostaje dokładnie bajty pliku (zaszyfrowane kluczem sesji) i ich skrót SHA-256.
        FakeRequest open = Assert.Single(ksef.RequestsTo("POST", "sessions/online"));
        IReadOnlyList<FakeRequest> sent = ksef.RequestsTo("POST", $"sessions/online/{Session}/invoices");
        Assert.Equal(2, sent.Count);
        foreach ((FakeRequest request, string file) in sent.Zip(files))
        {
            byte[] bytes = File.ReadAllBytes(file);
            Assert.Equal(bytes, FakeKsef.DecryptInvoice(open, request));
            Assert.Equal(Convert.ToBase64String(SHA256.HashData(bytes)), (string?)request.Json!["invoiceHash"]);
            Assert.Equal(bytes.Length, (long)request.Json!["invoiceSize"]!);
        }
        Assert.All(ksef.Requests.Where(r => !r.Path.StartsWith("security/", StringComparison.Ordinal)), r => Assert.Equal($"Bearer {FakeKsef.AccessToken}", r.Authorization));
        Assert.Single(ksef.RequestsTo("POST", $"sessions/online/{Session}/close"));
    }

    [Fact]
    public async Task Send_IssueDateInFuture_SendsNothing()
    {
        using FakeKsef ksef = OnlineSession();
        (TempDir dir, string[] files) = Invoices(2);
        using TempDir _ = dir;
        string future = DateTime.UtcNow.AddDays(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        File.WriteAllText(files[1], File.ReadAllText(files[1]).Replace("<P_1>2025-09-01</P_1>", $"<P_1>{future}</P_1>", StringComparison.Ordinal));

        CliResult result = await ksef.RunAsync(dir.Path, ["send", .. files]);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdoutBytes);
        Assert.Contains($"{files[1]}: data wystawienia P_1 {future} jest w przyszłości — KSeF ją odrzuci.", result.Stderr);
        Assert.Empty(ksef.RequestsTo("POST", "sessions/online"));
    }

    [Fact]
    public async Task Send_SessionTemporarilyUnavailable_ContinuesInNewSession()
    {
        // KSeF 21184: faktury 2 nie przyjęto, a sesja jest niedostępna — MF zaleca nową sesję i ponowienie.
        // Faktura 1 zostaje w pierwszej sesji, 2 i 3 idą w drugiej; każda z numerem swojej sesji (do UPO).
        const string Second = "20261030-SO-394363C000-F9191A9DA9-99";
        using FakeKsef ksef = OnlineSession(invoice: n => n == 2 ? FakeResponse.Error(400, 21184, "Sesja tymczasowo niedostępna") : null);
        ksef.OnSequence("POST", "sessions/online",
            FakeResponse.Json(new { referenceNumber = Session, validUntil = DateTimeOffset.UtcNow.AddHours(12) }),
            FakeResponse.Json(new { referenceNumber = Second, validUntil = DateTimeOffset.UtcNow.AddHours(12) }));
        int sentInSecond = 1;
        ksef.On("POST", $"sessions/online/{Second}/invoices", (_, _) =>
            FakeResponse.Json(new { referenceNumber = InvoiceReference(Interlocked.Increment(ref sentInSecond)) }));
        ksef.On("GET", $@"sessions/{Second}/invoices/20261030-EE-(\d{{10}})-0000000000-00", (_, match) =>
            InvoiceStatus(200, "Sukces", KsefNumber(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))));
        ksef.On("POST", $"sessions/online/{Second}/close", (_, _) => new FakeResponse(204));
        (TempDir dir, string[] files) = Invoices(3);
        using TempDir _ = dir;

        CliResult result = await ksef.RunAsync(dir.Path, ["send", .. files]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            $"{KsefNumber(1)}\t{files[0]}\t{Session}\n{KsefNumber(2)}\t{files[1]}\t{Second}\n{KsefNumber(3)}\t{files[2]}\t{Second}\n",
            result.Stdout);
        Assert.Equal($"ksef: sesja {Session} tymczasowo niedostępna (KSeF 21184) — wysyłam dalej w nowej sesji.\n", result.Stderr);
        // Druga sesja ma własny klucz AES: faktura 2 odszyfrowana jego kluczem to dokładnie bajty pliku.
        FakeRequest secondOpen = ksef.RequestsTo("POST", "sessions/online")[1];
        FakeRequest resent = ksef.RequestsTo("POST", $"sessions/online/{Second}/invoices")[0];
        Assert.Equal(File.ReadAllBytes(files[1]), FakeKsef.DecryptInvoice(secondOpen, resent));
        Assert.Single(ksef.RequestsTo("POST", $"sessions/online/{Session}/close"));
        Assert.Single(ksef.RequestsTo("POST", $"sessions/online/{Second}/close"));
    }

    [Fact]
    public async Task Send_ConnectionDropsMidRun_KeepsNumbersOfAcceptedInvoices()
    {
        // Najgorszy przypadek: KSeF przyjął fakturę 1 i 3, przy 2 zerwało się połączenie. Numery KSeF
        // przyjętych faktur muszą trafić na stdout — ponowna wysyłka całości skończyłaby się duplikatami.
        using FakeKsef ksef = OnlineSession(invoice: n => n == 2 ? FakeResponse.Abort : null);
        (TempDir dir, string[] files) = Invoices(3);
        using TempDir _ = dir;

        CliResult result = await ksef.RunAsync(dir.Path, ["send", .. files]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal($"{KsefNumber(1)}\t{files[0]}\t{Session}\n{KsefNumber(3)}\t{files[2]}\t{Session}\n", result.Stdout);
        Assert.StartsWith($"ksef: {files[1]}: ", result.Stderr);
        Assert.DoesNotContain("   at ", result.Stderr);
        Assert.Single(ksef.RequestsTo("POST", $"sessions/online/{Session}/close"));
    }

    [Fact]
    public async Task Send_ResponseLost_FindsInvoiceInSessionByHash()
    {
        // KSeF przyjął fakturę 2, ale odpowiedź się urwała. Zamiast zgłaszać ją jako niewysłaną (ponowienie po
        // zmianie numeru dałoby duplikat), ksef szuka jej w sesji po skrócie SHA-256 pliku.
        using FakeKsef ksef = OnlineSession(invoice: n => n == 2 ? FakeResponse.Abort : null);
        (TempDir dir, string[] files) = Invoices(3);
        using TempDir _ = dir;
        string lostHash = Convert.ToBase64String(SHA256.HashData(File.ReadAllBytes(files[1])));
        ksef.On("GET", $"sessions/{Session}/invoices", (_, _) => FakeResponse.Json(new
        {
            invoices = new[] { new { ordinalNumber = 2, referenceNumber = InvoiceReference(2), invoiceHash = lostHash, invoicingDate = DateTimeOffset.UtcNow, status = new { code = 100, description = "W trakcie" } } },
        }));

        CliResult result = await ksef.RunAsync(dir.Path, ["send", .. files]);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Equal(
            $"{KsefNumber(1)}\t{files[0]}\t{Session}\n{KsefNumber(2)}\t{files[1]}\t{Session}\n{KsefNumber(3)}\t{files[2]}\t{Session}\n",
            result.Stdout);
        Assert.Contains($"{files[1]}: odpowiedź KSeF na wysyłkę nie dotarła, ale faktura jest w sesji {Session}", result.Stderr);
        Assert.Equal(3, ksef.RequestsTo("POST", $"sessions/online/{Session}/invoices").Count);
    }

    [Fact]
    public async Task Send_StatusUnavailable_ReportsInvoiceReferenceForLaterCheck()
    {
        using FakeKsef ksef = OnlineSession(status: n => n == 1 ? FakeResponse.Abort : InvoiceStatus(200, "Sukces", KsefNumber(n)));
        (TempDir dir, string[] files) = Invoices(2);
        using TempDir _ = dir;

        CliResult result = await ksef.RunAsync(dir.Path, ["send", .. files]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal($"{KsefNumber(2)}\t{files[1]}\t{Session}\n", result.Stdout);
        Assert.Contains($"{files[0]}: brak statusu faktury {InvoiceReference(1)}", result.Stderr);
    }

    [Fact]
    public async Task Send_Rejected_ShowsKsefCodeDetailsAndExtensions()
    {
        using FakeKsef ksef = OnlineSession(status: n => n == 1
            ? InvoiceStatus(440, "Duplikat faktury", details: ["Duplikat faktury."],
                extensions: new() { ["originalKsefNumber"] = KsefNumber(99), ["originalSessionReferenceNumber"] = Session })
            : InvoiceStatus(200, "Sukces", KsefNumber(n)));
        (TempDir dir, string[] files) = Invoices(2);
        using TempDir _ = dir;

        CliResult result = await ksef.RunAsync(dir.Path, ["send", .. files]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal($"{KsefNumber(2)}\t{files[1]}\t{Session}\n", result.Stdout);
        Assert.Equal($"ksef: {files[0]}: KSeF 440 Duplikat faktury (Duplikat faktury.) [originalKsefNumber={KsefNumber(99)}, originalSessionReferenceNumber={Session}]\n", result.Stderr);
    }

    [Fact]
    public async Task Send_Json_SeparatesKsefCodeFromHttpStatus()
    {
        using FakeKsef ksef = OnlineSession(invoice: n => n == 1 ? FakeResponse.Error(400, 21111, "Nieprawidłowe dane faktury") : null);
        (TempDir dir, string[] files) = Invoices(2);
        using TempDir _ = dir;

        CliResult result = await ksef.RunAsync(dir.Path, ["send", "--json", .. files]);

        Assert.Equal(1, result.ExitCode);
        JsonArray results = JsonNode.Parse(result.Stdout)!.AsArray();
        Assert.Equal(2, results.Count);
        Assert.Equal(files[0], (string?)results[0]!["file"]);
        Assert.False((bool)results[0]!["accepted"]!);
        Assert.Equal(21111, (int?)results[0]!["code"]);
        Assert.Equal(400, (int?)results[0]!["httpStatus"]);
        Assert.Equal(KsefNumber(2), (string?)results[1]!["ksefNumber"]);
        Assert.Equal(200, (int?)results[1]!["code"]);
        Assert.Null(results[1]!["httpStatus"]);
    }

    [Fact]
    public async Task Send_WaitsWhileInvoiceIsProcessed()
    {
        int polls = 0;
        using FakeKsef ksef = OnlineSession(status: n => Interlocked.Increment(ref polls) switch
        {
            1 => InvoiceStatus(100, "Faktura przyjęta do dalszego przetwarzania"),
            2 => InvoiceStatus(150, "Trwa przetwarzanie"),
            _ => InvoiceStatus(200, "Sukces", KsefNumber(n)),
        });
        (TempDir dir, string[] files) = Invoices(1);
        using TempDir _ = dir;

        CliResult result = await ksef.RunAsync(dir.Path, ["send", .. files]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"{KsefNumber(1)}\t{files[0]}\t{Session}\n", result.Stdout);
        Assert.Equal(3, ksef.RequestsTo("GET", $"sessions/{Session}/invoices/.*").Count);
    }

    [Fact]
    public async Task Send_ShortRateLimit_WaitsRetryAfterAndRetries()
    {
        int calls = 0;
        using FakeKsef ksef = OnlineSession(invoice: _ => Interlocked.Increment(ref calls) == 1
            ? new FakeResponse(429, JsonSerializer.Serialize(new { status = new { code = 429, description = "Przekroczono limit 8 żądań na sekundę.", details = Array.Empty<string>() } }),
                Headers: new Dictionary<string, string> { ["Retry-After"] = "1" })
            : null);
        (TempDir dir, string[] files) = Invoices(1);
        using TempDir _ = dir;

        CliResult result = await ksef.RunAsync(dir.Path, ["send", .. files]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stderr);
        Assert.Equal(2, ksef.RequestsTo("POST", $"sessions/online/{Session}/invoices").Count);
    }

    [Fact]
    public async Task Send_LongRateLimit_FailsAtOnceWithKsefMessage()
    {
        using FakeKsef ksef = OnlineSession(invoice: _ =>
            new FakeResponse(429, JsonSerializer.Serialize(new { status = new { code = 429, description = "Przekroczono limit 20 żądań na godzinę.", details = Array.Empty<string>() } }),
                Headers: new Dictionary<string, string> { ["Retry-After"] = "3300" }));
        (TempDir dir, string[] files) = Invoices(1);
        using TempDir _ = dir;

        CliResult result = await ksef.RunAsync(dir.Path, ["send", .. files]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.StartsWith($"ksef: {files[0]}: HTTP 429 ", result.Stderr);
        Assert.Single(ksef.RequestsTo("POST", $"sessions/online/{Session}/invoices"));
    }

    [Fact]
    public async Task Send_CloseFails_WarnsButKeepsResults()
    {
        using FakeKsef ksef = OnlineSession();
        ksef.On("POST", $"sessions/online/{Session}/close", (_, _) => FakeResponse.Abort);
        (TempDir dir, string[] files) = Invoices(1);
        using TempDir _ = dir;

        CliResult result = await ksef.RunAsync(dir.Path, ["send", .. files]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"{KsefNumber(1)}\t{files[0]}\t{Session}\n", result.Stdout);
        Assert.StartsWith($"ksef: sesja {Session}: nie udało się jej zamknąć", result.Stderr);
    }

    [Fact]
    public async Task Send_OpenSessionFails_CleanErrorAndNothingSent()
    {
        using FakeKsef ksef = OnlineSession();
        ksef.On("POST", "sessions/online", (_, _) => FakeResponse.Error(400, 21401, "Nieprawidłowy klucz szyfrujący"));
        (TempDir dir, string[] files) = Invoices(1);
        using TempDir _ = dir;

        CliResult result = await ksef.RunAsync(dir.Path, ["send", .. files]);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Contains("21401", result.Stderr);
        Assert.DoesNotContain("   at ", result.Stderr);
        Assert.Empty(ksef.RequestsTo("POST", $"sessions/online/{Session}/invoices"));
    }
}

public class GetOrchestrationTests
{
    private const string Nip = "5908996669";
    private const string KsefA = "5908996669-20261030-7C4E9AC00000-D0";
    private const string KsefB = "5908996669-20261030-7C4EA1C00000-FA";

    private static string InvoiceXml(string number) => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Faktura><Nr>{number}</Nr><Opis>Zażółć gęślą jaźń</Opis></Faktura>";

    private static FakeKsef Invoices(params string[] available)
    {
        FakeKsef ksef = new();
        ksef.On("GET", @"invoices/ksef/(.+)", (_, match) => available.Contains(match.Groups[1].Value)
            ? FakeResponse.Xml(InvoiceXml(match.Groups[1].Value))
            : FakeResponse.Error(400, 21164, "Faktura o podanym identyfikatorze nie istnieje."));
        return ksef;
    }

    private static TempDir LoggedIn()
    {
        TempDir dir = new();
        FakeKsef.WriteLoginSession(dir.Path, Nip);
        return dir;
    }

    [Fact]
    public async Task Get_One_WritesXmlToStdoutWithoutBom()
    {
        using FakeKsef ksef = Invoices(KsefA);
        using TempDir dir = LoggedIn();

        CliResult result = await ksef.RunAsync(dir.Path, "get", KsefA);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new UTF8Encoding(false).GetBytes(InvoiceXml(KsefA)), result.StdoutBytes);
    }

    [Fact]
    public async Task Get_NotYetAvailable_RetriesUntilAvailable()
    {
        using FakeKsef ksef = new();
        ksef.OnSequence("GET", $"invoices/ksef/{KsefA}",
            FakeResponse.Error(400, 21165, "Faktura została przetworzona, ale nie jest jeszcze dostępna."),
            FakeResponse.Xml(InvoiceXml(KsefA)));
        using TempDir dir = LoggedIn();

        CliResult result = await ksef.RunAsync(dir.Path, "get", KsefA);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stderr);
        Assert.Equal(InvoiceXml(KsefA), result.Stdout);
        Assert.Equal(2, ksef.RequestsTo("GET", $"invoices/ksef/{KsefA}").Count);
    }

    [Fact]
    public async Task Get_Dir_FetchesOnlyMissing_InInputOrder()
    {
        using FakeKsef ksef = Invoices(KsefA, KsefB);
        using TempDir dir = LoggedIn();
        string outDir = dir.File("faktury");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, $"{KsefA}.xml"), "już pobrana");

        CliResult result = await ksef.RunAsync(dir.Path, Encoding.UTF8.GetBytes($"{KsefB}\tFV/2\n{KsefA}\tFV/1\n"), "get", "-d", outDir);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"{outDir}/{KsefB}.xml\n{outDir}/{KsefA}.xml\n", result.Stdout);
        Assert.Equal("już pobrana", File.ReadAllText(Path.Combine(outDir, $"{KsefA}.xml")));
        Assert.Equal(InvoiceXml(KsefB), File.ReadAllText(Path.Combine(outDir, $"{KsefB}.xml")));
        Assert.Empty(ksef.RequestsTo("GET", $"invoices/ksef/{KsefA}"));
    }

    [Fact]
    public async Task Get_OneMissing_OthersStillSaved()
    {
        using FakeKsef ksef = Invoices(KsefB);
        using TempDir dir = LoggedIn();
        string outDir = dir.File("faktury");

        CliResult result = await ksef.RunAsync(dir.Path, "get", "-d", outDir, KsefA, KsefB);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal($"{outDir}/{KsefB}.xml\n", result.Stdout);
        Assert.StartsWith($"ksef: {KsefA}: ", result.Stderr);
        Assert.Contains("21164", result.Stderr);
        Assert.False(File.Exists(Path.Combine(outDir, $"{KsefA}.xml")));
        Assert.Empty(Directory.GetFiles(outDir, "*.tmp"));
    }

    [Fact]
    public async Task Get_ConnectionDrops_CleanError()
    {
        using FakeKsef ksef = new();
        ksef.On("GET", "invoices/ksef/.*", (_, _) => FakeResponse.Abort);
        using TempDir dir = LoggedIn();

        CliResult result = await ksef.RunAsync(dir.Path, "get", KsefA);

        // Raz (2026-10-30, make test-aot) padło bez treści asercji i nie dało się tego powtórzyć
        // (0 na 250 pełnych przebiegów JIT/AOT) — przy następnym razie komunikat mówi, co się stało.
        string diagnostics = $"exit {result.ExitCode}, url {ksef.Url}, żądania: [{string.Join(", ", ksef.Requests.Select(r => $"{r.Method} {r.Path}"))}], "
            + $"stdout: {result.Stdout}, stderr: {result.Stderr}";
        Assert.True(result.ExitCode == 1, diagnostics);
        Assert.True(result.Stdout.Length == 0, diagnostics);
        Assert.True(result.Stderr.StartsWith("ksef: ", StringComparison.Ordinal), diagnostics);
        Assert.False(result.Stderr.Contains("   at ", StringComparison.Ordinal), diagnostics);
    }
}

public class UpoOrchestrationTests
{
    private const string Nip = "5908996669";
    private const string KsefA = "5908996669-20261030-7C4E9AC00000-D0";
    private const string KsefB = "5908996669-20261030-7C4EA1C00000-FA";
    private const string Session = "20261030-SO-394363C000-F9191A9DA9-98";

    private static FakeResponse SessionStatus(int code, string description, params string[] upoPages) => FakeResponse.Json(new
    {
        status = new { code, description, details = Array.Empty<string>() },
        upo = upoPages.Length == 0 ? null : new
        {
            pages = upoPages.Select(p => new { referenceNumber = p, downloadUrl = $"https://example.invalid/{p}", downloadUrlExpirationDate = DateTimeOffset.UtcNow.AddHours(1) }),
        },
        dateCreated = DateTimeOffset.UtcNow,
        dateUpdated = DateTimeOffset.UtcNow,
    });

    /// <summary>Status sesji z licznikami faktur — z nich `upo` rozpoznaje, czy brak UPO faktury jest chwilowy.</summary>
    private static FakeResponse SessionStatusWithCounts(int code, string description, int invoices, int? successful, int? failedInvoices = null) => FakeResponse.Json(new
    {
        status = new { code, description, details = Array.Empty<string>() },
        invoiceCount = invoices,
        successfulInvoiceCount = successful,
        failedInvoiceCount = failedInvoices,
        validUntil = DateTimeOffset.UtcNow.AddHours(11),
        dateCreated = DateTimeOffset.UtcNow,
        dateUpdated = DateTimeOffset.UtcNow,
    });

    private static FakeResponse UpoNotFound() => FakeResponse.Error(400, 21178, "Nie znaleziono UPO dla podanych kryteriów");

    private static string Upo(string id) => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Potwierdzenie><Id>{id}</Id></Potwierdzenie>";

    private static FakeKsef WithUpo(params FakeResponse[] statuses)
    {
        FakeKsef ksef = new();
        ksef.OnSequence("GET", $"sessions/{Session}", statuses);
        ksef.On("GET", $"sessions/{Session}/invoices/ksef/(.+)/upo", (_, match) => FakeResponse.Xml(Upo(match.Groups[1].Value)));
        ksef.On("GET", $"sessions/{Session}/upo/(.+)", (_, match) => FakeResponse.Xml(Upo(match.Groups[1].Value)));
        return ksef;
    }

    private static TempDir LoggedIn()
    {
        TempDir dir = new();
        FakeKsef.WriteLoginSession(dir.Path, Nip);
        return dir;
    }

    [Fact]
    public async Task Upo_FromSendOutput_OpenSession_SavesEachInvoiceUpoWithoutWaiting()
    {
        // UPO faktury KSeF wydaje razem z numerem KSeF, także przy otwartej sesji (np. innego programu, który jej nie zamknął).
        using FakeKsef ksef = WithUpo(SessionStatus(100, "Sesja interaktywna otwarta"));
        using TempDir dir = LoggedIn();
        string outDir = dir.File("upo");
        byte[] sendOutput = Encoding.UTF8.GetBytes($"{KsefA}\ta.xml\t{Session}\n{KsefB}\tb.xml\t{Session}\n");

        CliResult result = await ksef.RunAsync(dir.Path, sendOutput, "upo", "-d", outDir);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stderr);
        Assert.Equal($"{outDir}/{KsefA}.upo.xml\n{outDir}/{KsefB}.upo.xml\n", result.Stdout);
        Assert.Equal(Upo(KsefB), File.ReadAllText(Path.Combine(outDir, $"{KsefB}.upo.xml")));
        Assert.Empty(ksef.RequestsTo("GET", $"sessions/{Session}"));
    }

    [Fact]
    public async Task Upo_InvoiceStillProcessed_WaitsAndRetries()
    {
        using FakeKsef ksef = WithUpo(SessionStatusWithCounts(100, "Sesja interaktywna otwarta", invoices: 1, successful: null));
        ksef.OnSequence("GET", $"sessions/{Session}/invoices/ksef/{KsefA}/upo", UpoNotFound(), FakeResponse.Xml(Upo(KsefA)));
        using TempDir dir = LoggedIn();

        CliResult result = await ksef.RunAsync(dir.Path, "upo", "--session", Session, KsefA);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stderr);
        Assert.Equal(Upo(KsefA), result.Stdout);
        Assert.Equal(2, ksef.RequestsTo("GET", $"sessions/{Session}/invoices/ksef/{KsefA}/upo").Count);
        Assert.Single(ksef.RequestsTo("GET", $"sessions/{Session}"));
    }

    [Fact]
    public async Task Upo_Collective_OpenSession_ExplainsAtOnce()
    {
        using FakeKsef ksef = WithUpo(SessionStatusWithCounts(100, "Sesja interaktywna otwarta", invoices: 1, successful: 1));
        using TempDir dir = LoggedIn();

        CliResult result = await ksef.RunAsync(dir.Path, "upo", "--collective", "--session", Session);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.StartsWith($"ksef: sesja {Session}: wciąż otwarta (wygaśnie ", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("bez --collective", result.Stderr, StringComparison.Ordinal);
        Assert.Single(ksef.RequestsTo("GET", $"sessions/{Session}"));
        Assert.Empty(ksef.RequestsTo("GET", $"sessions/{Session}/upo/.*"));
    }

    [Fact]
    public async Task Upo_Collective_ClosedSession_WaitsUntilProcessed()
    {
        using FakeKsef ksef = WithUpo(SessionStatus(170, "Sesja zamknięta"), SessionStatus(200, "Sesja przetworzona", "UPO-1"));
        using TempDir dir = LoggedIn();

        CliResult result = await ksef.RunAsync(dir.Path, "upo", "--collective", "--session", Session);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Upo("UPO-1"), result.Stdout);
        Assert.Equal(2, ksef.RequestsTo("GET", $"sessions/{Session}").Count);
    }

    [Fact]
    public async Task Upo_Collective_SavesEveryPage()
    {
        using FakeKsef ksef = WithUpo(SessionStatus(200, "Sesja przetworzona", "UPO-1", "UPO-2"));
        using TempDir dir = LoggedIn();
        string outDir = dir.File("upo");

        CliResult result = await ksef.RunAsync(dir.Path, "upo", "--collective", "--session", Session, "-d", outDir);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"{outDir}/{Session}-UPO-1.upo.xml\n{outDir}/{Session}-UPO-2.upo.xml\n", result.Stdout);
        Assert.Equal(Upo("UPO-2"), File.ReadAllText(Path.Combine(outDir, $"{Session}-UPO-2.upo.xml")));
    }

    [Fact]
    public async Task Upo_Collective_SinglePageToStdout()
    {
        using FakeKsef ksef = WithUpo(SessionStatus(200, "Sesja przetworzona", "UPO-1"));
        using TempDir dir = LoggedIn();

        CliResult result = await ksef.RunAsync(dir.Path, "upo", "--collective", "--session", Session);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Upo("UPO-1"), result.Stdout);
    }

    [Fact]
    public async Task Upo_Collective_MultiplePagesWithoutDir_Refuses()
    {
        using FakeKsef ksef = WithUpo(SessionStatus(200, "Sesja przetworzona", "UPO-1", "UPO-2"));
        using TempDir dir = LoggedIn();

        CliResult result = await ksef.RunAsync(dir.Path, "upo", "--collective", "--session", Session);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Contains("ma 2 stron", result.Stderr);
    }

    [Fact]
    public async Task Upo_Collective_NoPages_ExplainsWhy()
    {
        using FakeKsef ksef = WithUpo(SessionStatus(200, "Sesja przetworzona"));
        using TempDir dir = LoggedIn();

        CliResult result = await ksef.RunAsync(dir.Path, "upo", "--collective", "--session", Session);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("nie wystawił dla niej UPO", result.Stderr);
    }

    [Fact]
    public async Task Upo_Collective_UnsafePageReference_NotUsedAsFileName()
    {
        using FakeKsef ksef = WithUpo(SessionStatus(200, "Sesja przetworzona", "../../etc/passwd"));
        using TempDir dir = LoggedIn();
        string outDir = dir.File("upo");

        CliResult result = await ksef.RunAsync(dir.Path, "upo", "--collective", "--session", Session, "-d", outDir);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("nieoczekiwany numer strony UPO", result.Stderr);
        Assert.DoesNotContain(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories), f => f.Contains("passwd"));
        Assert.Empty(ksef.RequestsTo("GET", $"sessions/{Session}/upo/.*"));
    }

    [Fact]
    public async Task Upo_SessionFailed_ReportsKsefStatus()
    {
        // Brak UPO faktury + sesja odrzucona: powód podaje status sesji.
        using FakeKsef ksef = WithUpo(SessionStatus(445, "Błąd weryfikacji, brak poprawnych faktur"));
        ksef.On("GET", $"sessions/{Session}/invoices/ksef/{KsefA}/upo", (_, _) => UpoNotFound());
        using TempDir dir = LoggedIn();

        CliResult result = await ksef.RunAsync(dir.Path, "upo", "--session", Session, KsefA);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal($"ksef: sesja {Session}: KSeF 445 Błąd weryfikacji, brak poprawnych faktur\n", result.Stderr);
    }

    [Fact]
    public async Task Upo_InvoiceUpoMissing_OthersStillSaved()
    {
        // Sesja bez faktur w przetwarzaniu: brak UPO jest ostateczny — błąd od razu, bez czekania.
        using FakeKsef ksef = WithUpo(SessionStatusWithCounts(200, "Sesja przetworzona", invoices: 2, successful: 1, failedInvoices: 1));
        ksef.On("GET", $"sessions/{Session}/invoices/ksef/{KsefA}/upo", (_, _) => UpoNotFound());
        using TempDir dir = LoggedIn();
        string outDir = dir.File("upo");

        CliResult result = await ksef.RunAsync(dir.Path, "upo", "--session", Session, "-d", outDir, KsefA, KsefB);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal($"{outDir}/{KsefB}.upo.xml\n", result.Stdout);
        Assert.StartsWith($"ksef: {KsefA}: KSeF nie ma UPO tej faktury w sesji {Session}", result.Stderr, StringComparison.Ordinal);
        Assert.Single(ksef.RequestsTo("GET", $"sessions/{Session}/invoices/ksef/{KsefA}/upo"));
    }
}
