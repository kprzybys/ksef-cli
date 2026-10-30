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
        services.AddKSeFClient(options => options.BaseUrl = env.ApiUrl);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Klient z usługą kryptograficzną (szyfrowanie faktur kluczem publicznym MF). Bez hosta usług
    /// rozgrzewamy ją sami: pobranie certyfikatów MF przed pierwszym użyciem.
    /// </summary>
    public static async Task<ServiceProvider> CreateWithCryptographyAsync(KsefEnvironment env, CancellationToken cancellationToken)
    {
        ServiceCollection services = new();
        services.AddKSeFClient(options => options.BaseUrl = env.ApiUrl);
        services.AddCryptographyClient();
        ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<ICryptographyService>().WarmupAsync(cancellationToken);
        return provider;
    }
}
