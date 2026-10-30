using System.CommandLine;
using KSeF.Client.Clients;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Models.Lighthouse;
using KSeF.Client.DI;
using Microsoft.Extensions.DependencyInjection;

namespace KsefCli.Commands;

/// <summary>
/// `ksef status` — bez logowania: komunikaty Latarni MF o dostępności KSeF
/// oraz sprawdzenie, czy API wybranego środowiska odpowiada.
/// </summary>
public static class StatusCommand
{
    public static Command Create(Option<string> envOption)
    {
        Command command = new("status", "Dostępność KSeF (Latarnia MF) i połączenie z API środowiska");
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            KsefEnvironment env = KsefEnvironment.Parse(parseResult.GetRequiredValue(envOption));
            return await RunAsync(env, cancellationToken);
        });
        return command;
    }

    private static async Task<int> RunAsync(KsefEnvironment env, CancellationToken cancellationToken)
    {
        ServiceCollection services = new();
        services.AddKSeFClient(options => options.BaseUrl = env.ApiUrl);
        // Latarnia TEST służy do testów integracji z samą Latarnią; o dostępności KSeF informuje PROD.
        services.AddLighthouseClient(LighthouseEnvironmentsUris.PROD);
        services.AddSingleton<ICryptographyClient, CryptographyClient>();

        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        int exitCode = 0;

        Console.WriteLine("Latarnia MF (dostępność KSeF):");
        try
        {
            ILighthouseClient lighthouse = scope.ServiceProvider.GetRequiredService<ILighthouseClient>();
            KsefStatusResponse status = await lighthouse.GetStatusAsync(cancellationToken);
            Console.WriteLine($"  status: {status.Status}");
            foreach (Message message in status.Messages ?? [])
            {
                string end = message.End is { } e ? $" – {e:yyyy-MM-dd HH:mm}" : "";
                Console.WriteLine($"  [{message.Type}] {message.Start:yyyy-MM-dd HH:mm}{end}: {message.Title}");
            }
            if (status.Status != KsefStatus.Available)
            {
                exitCode = 2;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"  błąd: {ex.Message}");
            exitCode = 1;
        }

        Console.WriteLine($"API KSeF ({env.Name}, {env.ApiUrl}):");
        try
        {
            ICryptographyClient crypto = scope.ServiceProvider.GetRequiredService<ICryptographyClient>();
            var certificates = await crypto.GetPublicCertificatesAsync(cancellationToken);
            Console.WriteLine($"  odpowiada; certyfikaty kluczy publicznych MF: {certificates.Count}");
            foreach (var cert in certificates)
            {
                Console.WriteLine($"  - {string.Join(", ", cert.Usage)}: ważny do {cert.ValidTo:yyyy-MM-dd}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.WriteLine($"  błąd: {ex.Message}");
            exitCode = 1;
        }

        return exitCode;
    }
}
