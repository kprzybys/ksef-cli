using KSeF.Client.DI;

namespace KsefCli;

/// <summary>Środowisko API KSeF.</summary>
public sealed record KsefEnvironment(string Name, string ApiUrl)
{
    public static readonly KsefEnvironment Test = new("test", KsefEnvironmentsUris.TEST);
    public static readonly KsefEnvironment Demo = new("demo", KsefEnvironmentsUris.DEMO);
    public static readonly KsefEnvironment Prod = new("prod", KsefEnvironmentsUris.PROD);

    public static readonly string[] Names = ["test", "demo", "prod"];

    /// <summary>Zmienna środowiska z domyślnym środowiskiem (zamiast `-e` w każdej komendzie); jawne `-e` ma pierwszeństwo.</summary>
    public const string EnvVariable = "KSEF_ENV";

    /// <summary>Środowisko, gdy nie podano `-e`: z KSEF_ENV, a bez niej `test` — pomyłka w piaskownicy nic nie kosztuje.</summary>
    public static string DefaultName => Environment.GetEnvironmentVariable(EnvVariable) is { Length: > 0 } name ? name : "test";

    /// <summary>Co środowisko znaczy dla użytkownika — w komunikatach obok nazwy, bo `prod`/`demo`/`test` nie wszystkim nic mówią.</summary>
    public string Description => Describe(Name);

    public static string Describe(string name) => name switch
    {
        "prod" => "PRODUKCJA, prawdziwe faktury",
        "demo" => "próba generalna, faktury bez skutków prawnych",
        _ => "piaskownica MF, fikcyjne dane",
    };

    /// <summary>Adres weryfikacji faktur kodem QR (KOD I) — zależy od nazwy środowiska, nie od KSEF_API_URL.</summary>
    public string QrUrl => Name switch
    {
        "prod" => KsefQREnvironmentsUris.PROD,
        "demo" => KsefQREnvironmentsUris.DEMO,
        _ => KsefQREnvironmentsUris.TEST,
    };

    /// <summary>Zmienna środowiska z innym adresem API (np. symulator KSeF w testach); sesje zostają przy nazwie środowiska.</summary>
    public const string ApiUrlVariable = "KSEF_API_URL";

    public static KsefEnvironment Parse(string name)
    {
        KsefEnvironment env = name switch
        {
            "test" => Test,
            "demo" => Demo,
            "prod" => Prod,
            _ => throw new ArgumentException($"Nieznane środowisko: {name}. Dozwolone: {string.Join(", ", Names)}.", nameof(name)),
        };
        return Environment.GetEnvironmentVariable(ApiUrlVariable) is { Length: > 0 } url ? env with { ApiUrl = url } : env;
    }
}
