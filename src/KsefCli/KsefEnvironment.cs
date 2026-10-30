using KSeF.Client.DI;

namespace KsefCli;

/// <summary>Środowisko API KSeF.</summary>
public sealed record KsefEnvironment(string Name, string ApiUrl)
{
    public static readonly KsefEnvironment Test = new("test", KsefEnvironmentsUris.TEST);
    public static readonly KsefEnvironment Demo = new("demo", KsefEnvironmentsUris.DEMO);
    public static readonly KsefEnvironment Prod = new("prod", KsefEnvironmentsUris.PROD);

    public static readonly string[] Names = ["test", "demo", "prod"];

    public static KsefEnvironment Parse(string name) => name switch
    {
        "test" => Test,
        "demo" => Demo,
        "prod" => Prod,
        _ => throw new ArgumentException($"Nieznane środowisko: {name}. Dozwolone: {string.Join(", ", Names)}."),
    };
}
