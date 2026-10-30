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
}
