using System.CommandLine;
using KSeF.Client.Core.Exceptions;
using KSeF.Client.Core.Interfaces.Clients;
using Microsoft.Extensions.DependencyInjection;

namespace KsefCli.Commands;

/// <summary>`ksef logout` i `ksef session` — zamknięcie i przegląd lokalnych sesji KSeF.</summary>
public static class SessionCommands
{
    // internal: typ wyniku --json musi być widoczny dla CliJsonContext.
    internal sealed record SessionInfo(string Environment, string Nip, string ReferenceNumber, DateTimeOffset AccessValidUntil, DateTimeOffset RefreshValidUntil);

    public static Command CreateLogout(Option<string> envOption)
    {
        Option<string> nipOption = Nip.Option("NIP sesji (wymagany, gdy w środowisku jest kilka sesji)");

        Command command = new("logout", "Unieważnia sesję w KSeF i usuwa ją lokalnie");
        command.Options.Add(nipOption);
        command.SetAction(async (parseResult, cancellationToken) => await LogoutAsync(
            KsefEnvironment.Parse(parseResult.GetRequiredValue(envOption)),
            parseResult.GetValue(nipOption),
            cancellationToken));
        return command;
    }

    public static Command CreateList(Option<string> envOption)
    {
        Option<bool> allOption = new("--all", "-a") { Description = "Sesje ze wszystkich środowisk" };
        Option<bool> jsonOption = CliIo.JsonOption();

        Command command = new("session", "Lokalne sesje KSeF (bez tokenów): środowisko, NIP, ważność; bez -e i KSEF_ENV — ze wszystkich środowisk. Kod wyjścia 1, gdy brak sesji");
        command.Options.Add(allOption);
        command.Options.Add(jsonOption);
        // Bez -e i bez KSEF_ENV pokazujemy wszystkie środowiska: „gdzie jestem zalogowany” to częste pierwsze pytanie,
        // a domyślne `test` ukryłoby sesję produkcyjną.
        command.SetAction(parseResult => List(
            parseResult.GetValue(allOption) || IsUnchosen(parseResult, envOption) ? null : parseResult.GetRequiredValue(envOption),
            parseResult.GetValue(jsonOption)));
        return command;
    }

    private static bool IsUnchosen(ParseResult parseResult, Option<string> envOption) =>
        parseResult.GetResult(envOption) is null or { Implicit: true }
        && Environment.GetEnvironmentVariable(KsefEnvironment.EnvVariable) is null or "";

    private static async Task<int> LogoutAsync(KsefEnvironment env, string? nip, CancellationToken cancellationToken)
    {
        try
        {
            KsefSession session = SessionStore.Resolve(env.Name, nip);
            await using ServiceProvider provider = KsefServices.Create(env);
            using IServiceScope scope = provider.CreateScope();
            IActiveSessionsClient sessions = scope.ServiceProvider.GetRequiredService<IActiveSessionsClient>();

            if (session.RefreshToken.ValidUntil > DateTimeOffset.UtcNow)
            {
                // Refresh token wystarcza do unieważnienia i działa także po wygaśnięciu access tokenu.
                await sessions.RevokeCurrentSessionAsync(session.RefreshToken.Token, cancellationToken);
            }
            SessionStore.Delete(session);
            return 0;
        }
        catch (Exception ex) when (ex is SessionException or KsefApiException or HttpRequestException)
        {
            CliIo.Error(ex.Message);
            return 1;
        }
    }

    private static int List(string? environment, bool json)
    {
        IReadOnlyList<KsefSession> sessions;
        try
        {
            sessions = SessionStore.List(environment);
        }
        catch (SessionException ex)
        {
            CliIo.Error(ex.Message);
            return 1;
        }

        List<SessionInfo> infos = [.. sessions.Select(s =>
            new SessionInfo(s.Environment, s.Nip, s.ReferenceNumber, s.AccessToken.ValidUntil, s.RefreshToken.ValidUntil))];
        if (json)
        {
            CliIo.WriteJson(infos);
        }
        else
        {
            foreach (SessionInfo info in infos)
            {
                Console.WriteLine($"{info.Environment}\t{info.Nip}\tsesja ważna do {info.RefreshValidUntil.ToLocalTime():yyyy-MM-dd HH:mm}\t{KsefEnvironment.Describe(info.Environment)}");
            }
        }
        return infos.Count > 0 ? 0 : 1;
    }
}
