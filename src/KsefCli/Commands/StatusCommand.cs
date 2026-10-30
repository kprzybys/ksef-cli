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
    // internal: typ wyniku --json musi być widoczny dla CliJsonContext.
    internal sealed record LighthouseResult(string? Status, IReadOnlyList<Message> Messages, string? Error);

    internal sealed record CertificateResult(IReadOnlyList<string> Usage, DateTimeOffset ValidTo);

    internal sealed record ApiResult(string Environment, string Url, bool Reachable, IReadOnlyList<CertificateResult> Certificates, string? Error);

    internal sealed record StatusResult(LighthouseResult Lighthouse, ApiResult Api);

    public static Command Create(Option<string> envOption)
    {
        Option<bool> jsonOption = CliIo.JsonOption();

        Command command = new("status", "Dostępność KSeF (Latarnia MF) i połączenie z API środowiska. Kod wyjścia: 0 dostępny, 2 niedostępny/prace, 1 błąd połączenia");
        command.Options.Add(jsonOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            KsefEnvironment env = KsefEnvironment.Parse(parseResult.GetRequiredValue(envOption));
            return await RunAsync(env, parseResult.GetValue(jsonOption), cancellationToken);
        });
        return command;
    }

    private static async Task<int> RunAsync(KsefEnvironment env, bool json, CancellationToken cancellationToken)
    {
        ServiceCollection services = new();
        services.AddKsefClient(env);
        // Latarnia TEST służy do testów integracji z samą Latarnią; o dostępności KSeF informuje PROD.
        services.AddLighthouseClient(LighthouseEnvironmentsUris.PROD);
        services.AddSingleton<ICryptographyClient, CryptographyClient>();

        await using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        StatusResult result = new(
            await CheckLighthouseAsync(scope.ServiceProvider.GetRequiredService<ILighthouseClient>(), cancellationToken),
            await CheckApiAsync(env, scope.ServiceProvider.GetRequiredService<ICryptographyClient>(), cancellationToken));

        if (json)
        {
            CliIo.WriteJson(result);
        }
        else
        {
            Print(result);
        }

        if (result.Lighthouse.Error is not null || result.Api.Error is not null)
        {
            return 1;
        }
        return result.Lighthouse.Status == KsefStatus.Available ? 0 : 2;
    }

    private static async Task<LighthouseResult> CheckLighthouseAsync(ILighthouseClient lighthouse, CancellationToken cancellationToken)
    {
        try
        {
            KsefStatusResponse status = await lighthouse.GetStatusAsync(cancellationToken);
            return new LighthouseResult(status.Status, status.Messages ?? [], null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new LighthouseResult(null, [], ex.Message);
        }
    }

    private static async Task<ApiResult> CheckApiAsync(KsefEnvironment env, ICryptographyClient crypto, CancellationToken cancellationToken)
    {
        try
        {
            var certificates = await crypto.GetPublicCertificatesAsync(cancellationToken);
            return new ApiResult(env.Name, env.ApiUrl, true,
                [.. certificates.Select(c => new CertificateResult([.. c.Usage.Select(u => u.ToString())], c.ValidTo))], null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ApiResult(env.Name, env.ApiUrl, false, [], ex.Message);
        }
    }

    private static void Print(StatusResult result)
    {
        Console.WriteLine("Latarnia MF (dostępność KSeF):");
        if (result.Lighthouse.Error is { } lighthouseError)
        {
            CliIo.Error($"Latarnia MF: {lighthouseError}");
        }
        else
        {
            Console.WriteLine($"  status: {result.Lighthouse.Status}");
            foreach (Message message in result.Lighthouse.Messages)
            {
                string end = message.End is { } e ? $" – {e:yyyy-MM-dd HH:mm}" : "";
                Console.WriteLine($"  [{message.Type}] {message.Start:yyyy-MM-dd HH:mm}{end}: {message.Title}");
            }
        }

        Console.WriteLine($"API KSeF ({result.Api.Environment}, {result.Api.Url}):");
        if (result.Api.Error is { } apiError)
        {
            CliIo.Error($"API KSeF ({result.Api.Environment}): {apiError}");
            return;
        }
        Console.WriteLine($"  odpowiada; certyfikaty kluczy publicznych MF: {result.Api.Certificates.Count}");
        foreach (CertificateResult cert in result.Api.Certificates)
        {
            Console.WriteLine($"  - {string.Join(", ", cert.Usage)}: ważny do {cert.ValidTo:yyyy-MM-dd}");
        }
    }
}
