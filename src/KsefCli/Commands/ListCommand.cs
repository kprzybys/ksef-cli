using System.CommandLine;
using System.Globalization;
using KSeF.Client.Core.Exceptions;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Models.Invoices;
using Microsoft.Extensions.DependencyInjection;

namespace KsefCli.Commands;

/// <summary>
/// `ksef list` — metadane faktur z KSeF dla zalogowanego NIP-u. Domyślnie TSV bez nagłówka
/// (numer KSeF w pierwszej kolumnie, do `cut -f1 | ksef get`), z `--json` pełne metadane.
/// </summary>
public static class ListCommand
{
    private static readonly Dictionary<string, InvoiceSubjectType> Subjects = new(StringComparer.Ordinal)
    {
        ["buyer"] = InvoiceSubjectType.Subject2,
        ["seller"] = InvoiceSubjectType.Subject1,
        ["third"] = InvoiceSubjectType.Subject3,
        ["authorized"] = InvoiceSubjectType.SubjectAuthorized,
    };

    private static readonly Dictionary<string, DateType> DateTypes = new(StringComparer.Ordinal)
    {
        ["issue"] = DateType.Issue,
        ["invoicing"] = DateType.Invoicing,
        ["storage"] = DateType.PermanentStorage,
    };

    public static Command Create(Option<string> envOption)
    {
        Option<string> nipOption = Nip.Option("NIP sesji (wymagany, gdy w środowisku jest kilka sesji)");
        Option<string> subjectOption = new("--subject", "-s")
        {
            Description = "Rola zalogowanego podmiotu: buyer (faktury otrzymane), seller (wystawione), third (podmiot trzeci), authorized (podmiot upoważniony)",
            DefaultValueFactory = _ => "buyer",
        };
        subjectOption.AcceptOnlyFromAmong([.. Subjects.Keys]);
        Option<string> fromOption = new("--from")
        {
            Description = "Od: RRRR-MM-DD (początek dnia, czas lokalny) albo data i czas ISO 8601; domyślnie 1. dzień bieżącego miesiąca",
        };
        Option<string> toOption = new("--to")
        {
            Description = "Do: RRRR-MM-DD (włącznie) albo data i czas ISO 8601; domyślnie teraz",
        };
        Option<string> dateTypeOption = new("--date-type")
        {
            Description = "Której daty dotyczy zakres: issue (wystawienia), invoicing (przyjęcia w KSeF), storage (trwałego zapisu — do pobierania przyrostowego)",
            DefaultValueFactory = _ => "issue",
        };
        dateTypeOption.AcceptOnlyFromAmong([.. DateTypes.Keys]);
        Option<bool> jsonOption = CliIo.JsonOption();

        Command command = new("list", "Faktury w KSeF z zakresu dat (metadane): TSV bez nagłówka — numer KSeF, numer faktury, data wystawienia, NIP i nazwa sprzedawcy, identyfikator i nazwa nabywcy, brutto, waluta");
        command.Options.Add(nipOption);
        command.Options.Add(subjectOption);
        command.Options.Add(fromOption);
        command.Options.Add(toOption);
        command.Options.Add(dateTypeOption);
        command.Options.Add(jsonOption);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            DateTimeOffset now = DateTimeOffset.Now;
            DateTimeOffset from, to;
            try
            {
                from = parseResult.GetValue(fromOption) is { } f ? ParseDate(f, endOfDay: false) : ParseDate($"{now:yyyy-MM}-01", endOfDay: false);
                to = parseResult.GetValue(toOption) is { } t ? ParseDate(t, endOfDay: true) : now;
            }
            catch (FormatException ex)
            {
                CliIo.Error(ex.Message);
                return 1;
            }

            InvoiceQueryFilters filters = new()
            {
                SubjectType = Subjects[parseResult.GetRequiredValue(subjectOption)],
                DateRange = new DateRange { DateType = DateTypes[parseResult.GetRequiredValue(dateTypeOption)], From = from, To = to },
            };
            return await RunAsync(
                KsefEnvironment.Parse(parseResult.GetRequiredValue(envOption)),
                parseResult.GetValue(nipOption),
                filters,
                parseResult.GetValue(jsonOption),
                cancellationToken);
        });
        return command;
    }

    private static readonly string[] IsoFormats =
    [
        "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    /// <summary>Sama data to dzień w czasie lokalnym: początek dla „od”, koniec dla „do” (zakres włącznie).</summary>
    public static DateTimeOffset ParseDate(string value, bool endOfDay)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day))
        {
            DateTime local = endOfDay ? day.ToDateTime(TimeOnly.MaxValue) : day.ToDateTime(TimeOnly.MinValue);
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }
        // Tylko ISO 8601 — luźne parsowanie kultury niezmiennej wzięłoby 01/10/2026 za 10 stycznia.
        if (DateTimeOffset.TryParseExact(value, IsoFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset moment))
        {
            return moment;
        }
        throw new FormatException($"Niepoprawna data: {value} (oczekiwano RRRR-MM-DD albo ISO 8601).");
    }

    private static async Task<int> RunAsync(KsefEnvironment env, string? nip, InvoiceQueryFilters filters, bool json, CancellationToken cancellationToken)
    {
        if (filters.DateRange.From > filters.DateRange.To)
        {
            CliIo.Error("data „od” jest późniejsza niż „do”.");
            return 1;
        }

        try
        {
            KsefSession session = SessionStore.Resolve(env.Name, nip);
            await using ServiceProvider provider = KsefServices.Create(env);
            using IServiceScope scope = provider.CreateScope();
            IAuthorizationClient authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationClient>();
            IKSeFClient ksef = scope.ServiceProvider.GetRequiredService<IKSeFClient>();

            // Token pobierany przy każdej stronie: długie zapytanie może przekroczyć ważność access tokenu.
            AccessTokenProvider tokens = new(session, authorization);
            InvoiceQueryResult result = await InvoiceQuery.QueryAllAsync(filters, async (f, offset, size, ct) =>
                await KsefRetry.OnRateLimitAsync(async () =>
                    await ksef.QueryInvoiceMetadataAsync(f, await tokens.GetAsync(ct), offset, size, SortOrder.Asc, ct), ct),
                cancellationToken);

            if (json)
            {
                CliIo.WriteJson(result.Invoices);
            }
            else
            {
                foreach (InvoiceSummary invoice in result.Invoices)
                {
                    Console.Out.WriteLine(ToTsv(invoice));
                }
            }
            return 0;
        }
        catch (Exception ex) when (ex is SessionException or KsefApiException or HttpRequestException or InvalidOperationException)
        {
            CliIo.Error(ex.Message);
            return 1;
        }
    }

    public static string ToTsv(InvoiceSummary invoice) => string.Join('\t',
        invoice.KsefNumber,
        Field(invoice.InvoiceNumber),
        invoice.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        invoice.Seller?.Nip,
        Field(invoice.Seller?.Name),
        invoice.Buyer?.Identifier?.Value,
        Field(invoice.Buyer?.Name),
        invoice.GrossAmount.ToString("0.00", CultureInfo.InvariantCulture),
        invoice.Currency);

    // TSV nie ma cytowania: tabulator i koniec wiersza w tekście wystawcy (nazwy, numer faktury) zamieniamy na spację.
    private static string? Field(string? value) => value?.ReplaceLineEndings(" ").Replace('\t', ' ');
}
