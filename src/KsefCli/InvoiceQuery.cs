using KSeF.Client.Core.Models.Invoices;

namespace KsefCli;

/// <summary>Wynik pełnego zapytania o metadane: wszystkie faktury z zakresu, bez powtórzeń.</summary>
public sealed record InvoiceQueryResult(IReadOnlyList<InvoiceSummary> Invoices, DateTimeOffset? PermanentStorageHwmDate);

/// <summary>
/// Zapytanie o metadane faktur przez wszystkie strony. KSeF zwraca najwyżej 10 000 pozycji na zapytanie
/// (<see cref="PagedInvoiceResponse.IsTruncated"/>); wtedy zaczynamy od daty ostatniej pozycji
/// (sortowanie rosnące) i pomijamy już widziane numery KSeF — tak zaleca MF dla pobierania przyrostowego.
/// KSeF przyjmuje zakres dat najwyżej 100 dni (błąd 21405), więc dłuższy dzielimy na okna.
/// </summary>
public static class InvoiceQuery
{
    public const int PageSize = 250;

    /// <summary>Najwięcej pozycji, jakie KSeF zwraca na jedno zapytanie (dalej <see cref="PagedInvoiceResponse.IsTruncated"/>).</summary>
    public const int MaxResultsPerQuery = 10_000;

    // Z zapasem względem limitu 100 dni KSeF (granice okien i przesunięcia stref czasowych).
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(90);

    public delegate Task<PagedInvoiceResponse> PageFetcher(InvoiceQueryFilters filters, int pageOffset, int pageSize, CancellationToken cancellationToken);

    public static async Task<InvoiceQueryResult> QueryAllAsync(
        InvoiceQueryFilters filters, PageFetcher fetchPage, CancellationToken cancellationToken, int maxResultsPerQuery = MaxResultsPerQuery)
    {
        List<InvoiceSummary> invoices = [];
        HashSet<string> seen = [];
        DateTimeOffset? hwm = null;
        DateTimeOffset end = filters.DateRange.To ?? DateTimeOffset.Now;

        for (DateTimeOffset windowStart = filters.DateRange.From; windowStart <= end; windowStart += MaxWindow)
        {
            DateTimeOffset windowEnd = windowStart + MaxWindow < end ? windowStart + MaxWindow : end;
            InvoiceQueryFilters window = Clone(filters, windowStart, windowEnd);
            hwm = await QueryWindowAsync(window, fetchPage, invoices, seen, maxResultsPerQuery, cancellationToken) ?? hwm;
            if (windowEnd == end)
            {
                break;
            }
        }
        return new InvoiceQueryResult(invoices, hwm);
    }

    /// <summary>
    /// Jedno okno (≤ <see cref="MaxWindow"/>) przez wszystkie strony. Obcięcie do <see cref="MaxResultsPerQuery"/>:
    /// stronicujemy do wyczerpania limitu, dopiero potem zaczynamy od daty ostatniej pozycji (`pageOffset` od zera) —
    /// tak opisuje to OpenAPI KSeF („hasMore i isTruncated → zawęź dateRange od daty ostatniego rekordu”).
    /// Niezależnie od tego, czy KSeF ustawia isTruncated na każdej stronie obciętego zbioru, czy tylko na ostatniej.
    /// </summary>
    private static async Task<DateTimeOffset?> QueryWindowAsync(
        InvoiceQueryFilters filters, PageFetcher fetchPage, List<InvoiceSummary> invoices, HashSet<string> seen,
        int maxResultsPerQuery, CancellationToken cancellationToken)
    {
        DateTimeOffset? hwm = null;
        while (true)
        {
            PagedInvoiceResponse page;
            InvoiceSummary? last = null;
            int pageOffset = 0;
            int fetched = 0;
            bool limitReached;
            do
            {
                page = await fetchPage(filters, pageOffset++, PageSize, cancellationToken);
                hwm = page.PermanentStorageHwmDate ?? hwm;
                foreach (InvoiceSummary invoice in page.Invoices ?? [])
                {
                    last = invoice;
                    fetched++;
                    if (seen.Add(invoice.KsefNumber))
                    {
                        invoices.Add(invoice);
                    }
                }
                limitReached = page.IsTruncated && fetched >= maxResultsPerQuery;
            }
            while (page.HasMore && !limitReached);

            if (!page.HasMore || last is null)
            {
                return hwm;
            }

            DateTimeOffset next = DateOf(last, filters.DateRange.DateType);
            if (next <= filters.DateRange.From)
            {
                // Ponad limit faktur z tą samą datą — dalej się nie przesuniemy.
                throw new InvalidOperationException($"Ponad {maxResultsPerQuery:N0} faktur z datą {next:O}; zawęź zapytanie (np. --subject albo inny --date-type).");
            }
            filters.DateRange.From = next;
        }
    }

    // Okna i przesunięcia zmieniają tylko zakres dat — filtry użytkownika zostają nietknięte.
    private static InvoiceQueryFilters Clone(InvoiceQueryFilters filters, DateTimeOffset from, DateTimeOffset to) => new()
    {
        SubjectType = filters.SubjectType,
        DateRange = new DateRange
        {
            DateType = filters.DateRange.DateType,
            From = from,
            To = to,
            RestrictToPermanentStorageHwmDate = filters.DateRange.RestrictToPermanentStorageHwmDate,
        },
        KsefNumber = filters.KsefNumber,
        InvoiceNumber = filters.InvoiceNumber,
        Amount = filters.Amount,
        SellerNip = filters.SellerNip,
        BuyerIdentifier = filters.BuyerIdentifier,
        CurrencyCodes = filters.CurrencyCodes,
        InvoicingMode = filters.InvoicingMode,
        IsSelfInvoicing = filters.IsSelfInvoicing,
        FormType = filters.FormType,
        InvoiceTypes = filters.InvoiceTypes,
        HasAttachment = filters.HasAttachment,
    };

    public static DateTimeOffset DateOf(InvoiceSummary invoice, DateType dateType) => dateType switch
    {
        DateType.Issue => invoice.IssueDate,
        DateType.Invoicing => invoice.InvoicingDate,
        DateType.PermanentStorage => invoice.PermanentStorageDate,
        _ => throw new ArgumentOutOfRangeException(nameof(dateType)),
    };
}
