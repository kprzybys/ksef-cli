using KSeF.Client.Core.Interfaces.Services;
using KSeF.Client.DI;
using Microsoft.Extensions.DependencyInjection;

namespace KsefCli;

/// <summary>Klient oficjalnej biblioteki MF dla wybranego środowiska.</summary>
public static class KsefServices
{
    public static ServiceProvider Create(KsefEnvironment env)
    {
        ServiceCollection services = new();
        services.AddKsefClient(env);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Klient z usługą kryptograficzną (szyfrowanie faktur kluczem publicznym MF). Bez hosta usług
    /// rozgrzewamy ją sami: pobranie certyfikatów MF przed pierwszym użyciem.
    /// </summary>
    public static async Task<ServiceProvider> CreateWithCryptographyAsync(KsefEnvironment env, CancellationToken cancellationToken)
    {
        ServiceCollection services = new();
        services.AddKsefClient(env);
        services.AddCryptographyClient();
        ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ICryptographyService>().WarmupAsync(cancellationToken);
        return provider;
    }

    /// <summary>
    /// Rejestracja klienta MF zawsze tędy: AddKSeFClient resetuje opcje JSON biblioteki,
    /// więc zaraz po nim podpinamy konteksty generowane i algorytmy podpisu (Native AOT).
    /// </summary>
    public static IServiceCollection AddKsefClient(this IServiceCollection services, KsefEnvironment env)
    {
        services.AddKSeFClient(options => options.BaseUrl = env.ApiUrl);
        KsefJson.InstallInKsefClient();
        XmlDsigAlgorithms.Register();
        return services;
    }
}
