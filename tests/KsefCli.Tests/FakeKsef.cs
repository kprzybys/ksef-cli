using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KsefCli.Tests;

/// <summary>Żądanie, które dotarło do fałszywego KSeF: ścieżka bez prefiksu wersji (`sessions/online`).</summary>
internal sealed record FakeRequest(string Method, string Path, string? Authorization, string Body)
{
    // Biblioteka MF wysyła żądania w PascalCase, odpowiedzi czyta bez względu na wielkość liter.
    public JsonNode? Json => Body.Length > 0 ? JsonNode.Parse(Body, new JsonNodeOptions { PropertyNameCaseInsensitive = true }) : null;
}

/// <summary>Odpowiedź fałszywego KSeF; <see cref="Abort"/> zrywa połączenie (błąd sieci po stronie klienta).</summary>
internal sealed record FakeResponse(int Status, string Body = "", string ContentType = "application/json", IReadOnlyDictionary<string, string>? Headers = null)
{
    public static readonly FakeResponse Abort = new(-1);

    public static FakeResponse Json(object body, int status = 200) => new(status, JsonSerializer.Serialize(body));

    public static FakeResponse Xml(string body) => new(200, body, "application/xml");

    /// <summary>Błąd w formacie KSeF (ApiErrorResponse) — z niego biblioteka MF bierze kod do KsefApiException.</summary>
    public static FakeResponse Error(int status, int code, string description) =>
        Json(new { exception = new { exceptionDetailList = new[] { new { exceptionCode = code, exceptionDescription = description, details = Array.Empty<string>() } } } }, status);
}

/// <summary>
/// Lokalny serwer HTTP udający API KSeF: `ksef` uruchomiony z `KSEF_API_URL` rozmawia z nim przez prawdziwą
/// bibliotekę MF (te same ścieżki, JSON i obsługa błędów), więc testy przebiegów send/get/upo nie potrzebują sieci
/// i sprawdzają też binarkę Native AOT. Trasy: metoda + wyrażenie regularne na ścieżce, pierwsza pasująca wygrywa.
/// </summary>
internal sealed class FakeKsef : IDisposable
{
    public const string AccessToken = "fake-access-token";

    // Klucz „MF”: klient szyfruje nim klucz AES sesji, a test odszyfrowuje faktury i porównuje z plikami.
    private static readonly RSA EncryptionKey = RSA.Create(2048);

    private static readonly Lazy<string> EncryptionCertificate = new(() =>
    {
        CertificateRequest request = new("CN=Fałszywy KSeF", EncryptionKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Cert));
    });

    private HttpListener listener = new();
    private readonly List<(string Method, Regex Path, Func<FakeRequest, Match, FakeResponse> Handler)> routes = [];
    private readonly List<FakeRequest> requests = [];
    private readonly Task loop;

    public FakeKsef()
    {
        // Wolny port bierzemy od systemu i zwalniamy przed startem listenera — w tej chwili może go zająć
        // inny test albo połączenie wychodzące, więc przy kolizji próbujemy na kolejnym.
        for (int attempt = 1; ; attempt++)
        {
            int port;
            using (TcpListener probe = new(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            Url = $"http://127.0.0.1:{port}";
            // Po nieudanym starcie listener nie nadaje się do ponownego użycia — zawsze nowy.
            listener = new HttpListener();
            listener.Prefixes.Add(Url + "/");
            try
            {
                listener.Start();
                break;
            }
            catch (HttpListenerException) when (attempt < 10)
            {
                listener.Close();
            }
        }

        // Klucz publiczny MF do szyfrowania faktur — pobierany przy starcie `send`.
        On("GET", "security/public-key-certificates", (_, _) => FakeResponse.Json(new[]
        {
            Certificate("SymmetricKeyEncryption"),
            Certificate("KsefTokenEncryption"),
        }));
        loop = Task.Run(ServeAsync);
    }

    public string Url { get; private set; } = "";

    public IReadOnlyList<FakeRequest> Requests
    {
        get
        {
            lock (requests)
            {
                return [.. requests];
            }
        }
    }

    public IReadOnlyList<FakeRequest> RequestsTo(string method, string pathPattern) =>
        [.. Requests.Where(r => r.Method == method && Regex.IsMatch(r.Path, $"^{pathPattern}$"))];

    /// <summary>Dodaje trasę; późniejsze trasy mają pierwszeństwo, więc test może nadpisać domyślną.</summary>
    public FakeKsef On(string method, string pathPattern, Func<FakeRequest, Match, FakeResponse> handler)
    {
        lock (routes)
        {
            routes.Insert(0, (method, new Regex($"^{pathPattern}$"), handler));
        }
        return this;
    }

    /// <summary>Kolejne odpowiedzi przy kolejnych wywołaniach; ostatnia powtarza się.</summary>
    public FakeKsef OnSequence(string method, string pathPattern, params FakeResponse[] responses)
    {
        int calls = 0;
        return On(method, pathPattern, (_, _) => responses[Math.Min(Interlocked.Increment(ref calls) - 1, responses.Length - 1)]);
    }

    /// <summary>Sesja logowania zapisana tak, jak zostawia ją `ksef login` — token ważny, bez odświeżania.</summary>
    public static void WriteLoginSession(string runtimeDir, string nip)
    {
        string dir = Path.Combine(runtimeDir, "ksef");
        Directory.CreateDirectory(dir);
        DateTimeOffset until = DateTimeOffset.UtcNow.AddHours(1);
        File.WriteAllText(Path.Combine(dir, $"test-{nip}.json"), JsonSerializer.Serialize(new
        {
            environment = "test",
            nip,
            referenceNumber = "20261030-AU-0000000000-0000000000-00",
            accessToken = new { token = AccessToken, validUntil = until },
            refreshToken = new { token = "fake-refresh-token", validUntil = until },
        }));
    }

    /// <summary>Treść faktury z żądania wysyłki: klucz AES z otwarcia sesji (RSA-OAEP SHA-256), AES-256-CBC.</summary>
    public static byte[] DecryptInvoice(FakeRequest openSession, FakeRequest sendInvoice)
    {
        JsonNode encryption = openSession.Json!["encryption"]!;
        using Aes aes = Aes.Create();
        aes.Key = EncryptionKey.Decrypt(Convert.FromBase64String((string)encryption["encryptedSymmetricKey"]!), RSAEncryptionPadding.OaepSHA256);
        return aes.DecryptCbc(
            Convert.FromBase64String((string)sendInvoice.Json!["encryptedInvoiceContent"]!),
            Convert.FromBase64String((string)encryption["initializationVector"]!));
    }

    /// <summary>Token KSeF z żądania logowania tokenem (`token|znacznik czasu wyzwania w ms`, RSA-OAEP SHA-256).</summary>
    public static string DecryptKsefToken(FakeRequest submission) =>
        Encoding.UTF8.GetString(EncryptionKey.Decrypt(Convert.FromBase64String((string)submission.Json!["encryptedToken"]!), RSAEncryptionPadding.OaepSHA256));

    public Task<CliResult> RunAsync(string runtimeDir, byte[]? stdin, params string[] args) =>
        Ksef.RunAsync(runtimeDir, stdin, new Dictionary<string, string> { ["KSEF_API_URL"] = Url }, args);

    public Task<CliResult> RunAsync(string runtimeDir, params string[] args) => RunAsync(runtimeDir, null, args);

    public void Dispose()
    {
        listener.Close();
        try
        {
            loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Zamknięcie listenera kończy pętlę wyjątkiem.
        }
    }

    private static object Certificate(string usage) => new
    {
        certificate = EncryptionCertificate.Value,
        certificateId = usage,
        publicKeyId = usage,
        validFrom = DateTimeOffset.UtcNow.AddDays(-1),
        validTo = DateTimeOffset.UtcNow.AddDays(30),
        usage = new[] { usage },
    };

    private async Task ServeAsync()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(() => Handle(context));
        }
    }

    private void Handle(HttpListenerContext context)
    {
        string body;
        using (StreamReader reader = new(context.Request.InputStream, Encoding.UTF8))
        {
            body = reader.ReadToEnd();
        }
        string path = Regex.Replace(context.Request.Url!.AbsolutePath, "^/v2/", "");
        FakeRequest request = new(context.Request.HttpMethod, path, context.Request.Headers["Authorization"], body);
        lock (requests)
        {
            requests.Add(request);
        }

        FakeResponse response = FakeResponse.Error(404, 0, $"fałszywy KSeF: brak trasy {request.Method} {path}");
        lock (routes)
        {
            foreach ((string method, Regex pattern, Func<FakeRequest, Match, FakeResponse> handler) in routes)
            {
                if (method == request.Method && pattern.Match(path) is { Success: true } match)
                {
                    response = handler(request, match);
                    break;
                }
            }
        }

        if (ReferenceEquals(response, FakeResponse.Abort))
        {
            // Zapowiedziana treść, której nie ma: klient dostaje urwaną odpowiedź (HttpIOException), jak przy zerwanym łączu.
            context.Response.ContentLength64 = 1000;
            context.Response.OutputStream.Write("{"u8);
            context.Response.OutputStream.Flush();
            context.Response.Abort();
            return;
        }
        context.Response.StatusCode = response.Status;
        foreach ((string name, string value) in response.Headers ?? new Dictionary<string, string>())
        {
            context.Response.Headers[name] = value;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(response.Body);
        if (bytes.Length > 0)
        {
            context.Response.ContentType = response.ContentType + "; charset=utf-8";
        }
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }
}
