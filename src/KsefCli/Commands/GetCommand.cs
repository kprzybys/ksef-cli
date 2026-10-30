using System.CommandLine;
using System.Text;
using KSeF.Client.Core.Exceptions;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace KsefCli.Commands;

/// <summary>
/// `ksef get` — XML faktur z KSeF po numerze KSeF. Jeden numer bez `-d`: XML na stdout.
/// Z `-d`: pliki `&lt;numer KSeF&gt;.xml`, ścieżki na stdout; już pobrane pomijamy (faktura w KSeF się nie zmienia),
/// więc `ksef list | ksef get -d faktury/` można powtarzać.
/// </summary>
public static class GetCommand
{
    private const int NotYetAvailableCode = 21165;
    private static readonly TimeSpan NotYetAvailableTimeout = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    public static Command Create(Option<string> envOption)
    {
        Argument<string[]> numbersArgument = new("numery")
        {
            Description = "Numery KSeF; `-` albo brak = stdin, po jednym w wierszu (pierwsza kolumna TSV, np. z `ksef list`)",
            Arity = ArgumentArity.ZeroOrMore,
        };
        Option<string> dirOption = new("--dir", "-d")
        {
            Description = "Katalog na pliki <numer KSeF>.xml (tworzony w razie potrzeby); wymagany przy kilku numerach",
        };
        Option<string> nipOption = Nip.Option("NIP sesji (wymagany, gdy w środowisku jest kilka sesji)");

        Command command = new("get", "Pobiera XML faktur z KSeF po numerze KSeF. Kod wyjścia: 0 wszystkie pobrane, 1 jest błąd");
        command.Arguments.Add(numbersArgument);
        command.Options.Add(dirOption);
        command.Options.Add(nipOption);
        command.SetAction(async (parseResult, cancellationToken) => await RunAsync(
            KsefEnvironment.Parse(parseResult.GetRequiredValue(envOption)),
            parseResult.GetValue(nipOption),
            CliIo.ReadIdentifiers(parseResult.GetValue(numbersArgument) ?? []),
            parseResult.GetValue(dirOption),
            cancellationToken));
        return command;
    }

    private static async Task<int> RunAsync(KsefEnvironment env, string? nip, IReadOnlyList<string> numbers, string? dir, CancellationToken cancellationToken)
    {
        if (numbers.Count == 0)
        {
            CliIo.Error("brak numerów KSeF.");
            return 1;
        }
        if (dir is null && numbers.Distinct(StringComparer.Ordinal).Count() > 1)
        {
            CliIo.Error("kilka faktur naraz zapisuję tylko do katalogu — podaj -d <katalog>.");
            return 1;
        }
        if (numbers.FirstOrDefault(n => !RegexPatterns.KsefNumber.IsMatch(n)) is { } invalid)
        {
            CliIo.Error($"niepoprawny numer KSeF: {invalid}");
            return 1;
        }

        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
            if (numbers.All(n => File.Exists(PathFor(dir, n))))
            {
                // Wszystko już pobrane — bez sesji i bez sieci.
                foreach (string number in numbers.Distinct(StringComparer.Ordinal))
                {
                    Console.Out.WriteLine(PathFor(dir, number));
                }
                return 0;
            }
        }

        bool failed = false;
        try
        {
            KsefSession session = SessionStore.Resolve(env.Name, nip);
            await using ServiceProvider provider = KsefServices.Create(env);
            using IServiceScope scope = provider.CreateScope();
            AccessTokenProvider tokens = new(session, scope.ServiceProvider.GetRequiredService<IAuthorizationClient>());
            IKSeFClient ksef = scope.ServiceProvider.GetRequiredService<IKSeFClient>();

            foreach (string number in numbers.Distinct(StringComparer.Ordinal))
            {
                if (dir is not null && File.Exists(PathFor(dir, number)))
                {
                    Console.Out.WriteLine(PathFor(dir, number));
                    continue;
                }

                byte[] xml;
                try
                {
                    xml = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                        .GetBytes(await GetWhenAvailableAsync(ksef, tokens, number, cancellationToken));
                }
                catch (KsefApiException ex)
                {
                    CliIo.Error($"{number}: {ex.Message}");
                    failed = true;
                    continue;
                }

                if (dir is null)
                {
                    CliIo.WriteAllBytes(CliIo.StdStream, xml);
                }
                else
                {
                    CliIo.WriteFileAtomically(PathFor(dir, number), xml);
                    Console.Out.WriteLine(PathFor(dir, number));
                }
            }
        }
        catch (Exception ex) when (ex is SessionException or KsefApiException or HttpRequestException)
        {
            CliIo.Error(ex.Message);
            return 1;
        }
        return failed ? 1 : 0;
    }

    /// <summary>
    /// Faktura przyjęta przed chwilą bywa jeszcze niedostępna do pobrania (KSeF 21165 — „została przetworzona,
    /// ale nie jest jeszcze dostępna”): ponawiamy z limitem czasu, zamiast zrzucać to na użytkownika.
    /// </summary>
    private static async Task<string> GetWhenAvailableAsync(IKSeFClient ksef, AccessTokenProvider tokens, string number, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + NotYetAvailableTimeout;
        while (true)
        {
            try
            {
                return await KsefRetry.OnRateLimitAsync(async () =>
                    await ksef.GetInvoiceAsync(number, await tokens.GetAsync(cancellationToken), cancellationToken), cancellationToken);
            }
            catch (KsefApiException ex) when (IsNotYetAvailable(ex) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(RetryInterval, cancellationToken);
            }
        }
    }

    public static bool IsNotYetAvailable(KsefApiException ex) =>
        ex.ErrorResponse?.Exception?.ExceptionDetailList?.Any(d => d.ExceptionCode == NotYetAvailableCode) == true;

    private static string PathFor(string dir, string ksefNumber) => Path.Combine(dir, ksefNumber + ".xml");
}
