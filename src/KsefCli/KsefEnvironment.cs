using KSeF.Client.DI;

namespace KsefCli;

/// <summary>Środowisko API KSeF.</summary>
public sealed record KsefEnvironment(string Name, string ApiUrl)
{
    public static readonly KsefEnvironment Test = new("test", KsefEnvironmentsUris.TEST);
    public static readonly KsefEnvironment Demo = new("demo", KsefEnvironmentsUris.DEMO);
    public static readonly KsefEnvironment Prod = new("prod", KsefEnvironmentsUris.PROD);

    public static readonly string[] Names = ["test", "demo", "prod"];

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
