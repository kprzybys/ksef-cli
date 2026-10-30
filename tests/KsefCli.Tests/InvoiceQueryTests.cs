using KSeF.Client.Core.Models.Invoices;

namespace KsefCli.Tests;

public class InvoiceQueryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static InvoiceSummary Invoice(int n, DateTimeOffset? issued = null) => new()
    {
        KsefNumber = $"KSEF-{n:D5}",
        InvoiceNumber = $"FV/{n}",
        IssueDate = issued ?? Start.AddMinutes(n),
        InvoicingDate = Start.AddHours(n),
        PermanentStorageDate = Start.AddDays(n),
    };

    private static InvoiceQueryFilters Filters(DateTimeOffset from, DateTimeOffset to, DateType dateType = DateType.Issue) => new()
    {
        SubjectType = InvoiceSubjectType.Subject2,
        SellerNip = "5908996669",
        DateRange = new DateRange { DateType = dateType, From = from, To = to },
    };

    private static PagedInvoiceResponse Page(IEnumerable<InvoiceSummary> invoices, bool hasMore = false, bool truncated = false) =>
        new() { Invoices = [.. invoices], HasMore = hasMore, IsTruncated = truncated };

    /// <summary>Zapamiętuje każde wywołanie (kopię zakresu dat) i odpowiada według scenariusza.</summary>
    private sealed class Recorder(Func<InvoiceQueryFilters, int, PagedInvoiceResponse> respond)
    {
        public List<(DateTimeOffset From, DateTimeOffset? To, int Offset, int Size, InvoiceQueryFilters Filters)> Calls { get; } = [];

        public Task<PagedInvoiceResponse> FetchAsync(InvoiceQueryFilters filters, int offset, int size, CancellationToken _)
        {
            Calls.Add((filters.DateRange.From, filters.DateRange.To, offset, size, filters));
            return Task.FromResult(respond(filters, offset));
        }
    }

    [Fact]
    public async Task SinglePage_ReturnsInvoicesInOrder()
    {
        Recorder api = new((_, _) => Page([Invoice(1), Invoice(2)]));

        InvoiceQueryResult result = await InvoiceQuery.QueryAllAsync(Filters(Start, Start.AddDays(5)), api.FetchAsync, CancellationToken.None);

        Assert.Equal(["KSEF-00001", "KSEF-00002"], result.Invoices.Select(i => i.KsefNumber));
        Assert.Equal((0, InvoiceQuery.PageSize), (api.Calls[0].Offset, api.Calls[0].Size));
        Assert.Single(api.Calls);
    }

    [Fact]
    public async Task FollowsPagesWhileHasMore()
    {
        Recorder api = new((_, offset) => offset switch
        {
            0 => Page([Invoice(1)], hasMore: true),
            1 => Page([Invoice(2)], hasMore: true),
            _ => Page([Invoice(3)]),
        });

        InvoiceQueryResult result = await InvoiceQuery.QueryAllAsync(Filters(Start, Start.AddDays(5)), api.FetchAsync, CancellationToken.None);

        Assert.Equal([0, 1, 2], api.Calls.Select(c => c.Offset));
        Assert.Equal(3, result.Invoices.Count);
    }

    [Fact]
    public async Task EmptyResult_IsNotAnError()
    {
        Recorder api = new((_, _) => Page([]));

        InvoiceQueryResult result = await InvoiceQuery.QueryAllAsync(Filters(Start, Start.AddDays(5)), api.FetchAsync, CancellationToken.None);

        Assert.Empty(result.Invoices);
    }

    [Fact]
    public async Task Truncated_ContinuesFromLastDateAndSkipsDuplicates()
    {
        // Limit 3 na zapytanie: pierwsze obcięte po fakturach 1–3; drugie (od daty faktury 3) zwraca 3 ponownie oraz 4–5.
        Recorder api = new((filters, offset) => filters.DateRange.From == Start
            ? offset == 0 ? Page([Invoice(1), Invoice(2)], hasMore: true) : Page([Invoice(3)], hasMore: true, truncated: true)
            : Page([Invoice(3), Invoice(4), Invoice(5)]));

        InvoiceQueryResult result = await InvoiceQuery.QueryAllAsync(Filters(Start, Start.AddDays(5)), api.FetchAsync, CancellationToken.None, maxResultsPerQuery: 3);

        Assert.Equal(["KSEF-00001", "KSEF-00002", "KSEF-00003", "KSEF-00004", "KSEF-00005"], result.Invoices.Select(i => i.KsefNumber));
        Assert.Equal(Invoice(3).IssueDate, api.Calls[2].From);
        Assert.Equal(0, api.Calls[2].Offset); // nowe zapytanie zaczyna od pierwszej strony
    }

    [Theory]
    [InlineData(DateType.Issue)]
    [InlineData(DateType.Invoicing)]
    [InlineData(DateType.PermanentStorage)]
    public async Task Truncated_ContinuesFromTheQueriedDateType(DateType dateType)
    {
        Recorder api = new((filters, _) => filters.DateRange.From == Start ? Page([Invoice(1), Invoice(2)], hasMore: true, truncated: true) : Page([]));

        await InvoiceQuery.QueryAllAsync(Filters(Start, Start.AddDays(30), dateType), api.FetchAsync, CancellationToken.None, maxResultsPerQuery: 2);

        Assert.Equal(InvoiceQuery.DateOf(Invoice(2), dateType), api.Calls[1].From);
    }

    [Fact]
    public async Task Truncated_WithoutProgress_FailsInsteadOfLooping()
    {
        // Cały limit zapytania to faktury z datą początku zakresu: przesunięcie nic nie da.
        Recorder api = new((_, _) => Page([Invoice(1, Start), Invoice(2, Start)], hasMore: true, truncated: true));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvoiceQuery.QueryAllAsync(Filters(Start, Start.AddDays(5)), api.FetchAsync, CancellationToken.None, maxResultsPerQuery: 2));

        Assert.Contains("Ponad 2 faktur", ex.Message);
        Assert.Single(api.Calls);
    }

    [Fact]
    public async Task Truncated_KeepsPagingUpToTheLimitBeforeMovingOn()
    {
        // KSeF może oznaczać isTruncated na każdej stronie obciętego zbioru: wtedy i tak stronicujemy do limitu,
        // inaczej dzień z ponad jedną stroną faktur dawałby fałszywy błąd braku postępu.
        DateTimeOffset day = Start.AddDays(1);
        Recorder api = new((filters, offset) => filters.DateRange.From == Start
            ? Page([Invoice(2 * offset + 1, day), Invoice(2 * offset + 2, day)], hasMore: true, truncated: true)
            : Page([Invoice(5, day), Invoice(6, day.AddHours(1))]));

        InvoiceQueryResult result = await InvoiceQuery.QueryAllAsync(Filters(Start, Start.AddDays(5)), api.FetchAsync, CancellationToken.None, maxResultsPerQuery: 4);

        Assert.Equal([0, 1, 0], api.Calls.Select(c => c.Offset));
        Assert.Equal(day, api.Calls[2].From);
        Assert.Equal(6, result.Invoices.Count);
    }

    [Fact]
    public async Task Truncated_ContinuesFromLastInvoiceOfThePage_EvenWhenAllAreDuplicates()
    {
        // Drugie zapytanie zwraca same powtórki z późniejszą datą — przesunięcie liczy się od ostatniej pozycji strony.
        DateTimeOffset later = Start.AddDays(2);
        Recorder api = new((filters, _) =>
            filters.DateRange.From == Start ? Page([Invoice(1, Start.AddDays(1)), Invoice(2, later)], hasMore: true, truncated: true)
            : filters.DateRange.From == later ? Page([Invoice(2, later), Invoice(2, later.AddDays(1))], hasMore: true, truncated: true)
            : Page([]));

        await InvoiceQuery.QueryAllAsync(Filters(Start, Start.AddDays(5)), api.FetchAsync, CancellationToken.None, maxResultsPerQuery: 2);

        Assert.Equal([Start, later, later.AddDays(1)], api.Calls.Select(c => c.From));
    }

    [Fact]
    public async Task TruncatedWithoutMore_IsComplete()
    {
        Recorder api = new((_, _) => Page([Invoice(1), Invoice(2)], truncated: true));

        InvoiceQueryResult result = await InvoiceQuery.QueryAllAsync(Filters(Start, Start.AddDays(5)), api.FetchAsync, CancellationToken.None, maxResultsPerQuery: 2);

        Assert.Single(api.Calls);
        Assert.Equal(2, result.Invoices.Count);
    }

    [Fact]
    public async Task LongRange_IsSplitIntoContiguousWindowsWithinKsefLimit()
    {
        DateTimeOffset end = Start.AddDays(365);
        Recorder api = new((_, _) => Page([]));

        await InvoiceQuery.QueryAllAsync(Filters(Start, end), api.FetchAsync, CancellationToken.None);

        Assert.Equal(5, api.Calls.Count); // 365 dni = 4 pełne okna po 90 dni + 5 dni
        Assert.Equal(Start, api.Calls[0].From);
        Assert.Equal(end, api.Calls[^1].To);
        for (int i = 0; i < api.Calls.Count; i++)
        {
            Assert.True(api.Calls[i].To!.Value - api.Calls[i].From <= TimeSpan.FromDays(100), $"okno {i} przekracza 100 dni");
            if (i > 0)
            {
                Assert.Equal(api.Calls[i - 1].To, api.Calls[i].From); // bez dziur między oknami
            }
        }
    }

    [Fact]
    public async Task Windows_DeduplicateInvoicesOnBoundaries()
    {
        // Faktura z datą równą granicy okien przychodzi z obu okien.
        DateTimeOffset boundary = Start + InvoiceQuery.MaxWindow;
        Recorder api = new((filters, _) => Page([Invoice(1, boundary)]));

        InvoiceQueryResult result = await InvoiceQuery.QueryAllAsync(Filters(Start, Start.AddDays(120)), api.FetchAsync, CancellationToken.None);

        Assert.Equal(2, api.Calls.Count);
        Assert.Single(result.Invoices);
    }

    [Fact]
    public async Task ExactlyOneWindow_IsOneQuery()
    {
        Recorder api = new((_, _) => Page([]));

        await InvoiceQuery.QueryAllAsync(Filters(Start, Start + InvoiceQuery.MaxWindow), api.FetchAsync, CancellationToken.None);

        Assert.Single(api.Calls);
    }

    [Fact]
    public async Task UserFilters_ArePassedOnAndNotMutated()
    {
        InvoiceQueryFilters filters = Filters(Start, Start.AddDays(200));
        Recorder api = new((f, _) => f.DateRange.From == Start ? Page([Invoice(1), Invoice(2)], hasMore: true, truncated: true) : Page([]));

        await InvoiceQuery.QueryAllAsync(filters, api.FetchAsync, CancellationToken.None, maxResultsPerQuery: 2);

        Assert.All(api.Calls, c =>
        {
            Assert.Equal(InvoiceSubjectType.Subject2, c.Filters.SubjectType);
            Assert.Equal("5908996669", c.Filters.SellerNip);
            Assert.Equal(DateType.Issue, c.Filters.DateRange.DateType);
        });
        Assert.Equal(Start, filters.DateRange.From);
        Assert.Equal(Start.AddDays(200), filters.DateRange.To);
    }
}

public class ListFormatTests
{
    [Fact]
    public void ParseDate_DayMeansLocalStartOrEndOfDay()
    {
        DateTimeOffset from = KsefCli.Commands.ListCommand.ParseDate("2026-10-01", endOfDay: false);
        DateTimeOffset to = KsefCli.Commands.ListCommand.ParseDate("2026-10-31", endOfDay: true);

        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0), from.DateTime);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 1)), from.Offset);
        Assert.Equal(new DateTime(2026, 10, 31), to.Date);
        Assert.True(to.TimeOfDay >= new TimeSpan(23, 59, 59));
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 31)), to.Offset); // po zmianie czasu
    }

    [Fact]
    public void ParseDate_AcceptsIsoWithOffset()
    {
        DateTimeOffset value = KsefCli.Commands.ListCommand.ParseDate("2026-10-01T12:30:00+02:00", endOfDay: true);

        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 30, 0, TimeSpan.FromHours(2)), value);
    }

    [Fact]
    public void ParseDate_AcceptsIsoWithoutOffsetAsLocal()
    {
        DateTimeOffset value = KsefCli.Commands.ListCommand.ParseDate("2026-10-01T12:30", endOfDay: false);

        Assert.Equal(new DateTime(2026, 10, 1, 12, 30, 0), value.DateTime);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 1, 12, 30, 0)), value.Offset);
    }

    [Theory]
    [InlineData("wczoraj")]
    [InlineData("2026-13-01")]
    [InlineData("01.10.2026x")]
    [InlineData("10/01/2026")] // format amerykański: nie zgadujemy, czy to 1 października, czy 10 stycznia
    [InlineData("01/10/2026")]
    [InlineData("1 Oct 2026")]
    public void ParseDate_RejectsGarbage(string value) =>
        Assert.Throws<FormatException>(() => KsefCli.Commands.ListCommand.ParseDate(value, endOfDay: false));

    [Fact]
    public void ToTsv_HasFixedColumnsAndNoEmbeddedSeparators()
    {
        InvoiceSummary invoice = new()
        {
            KsefNumber = "5908996669-20261030-0123456789AB-CD",
            InvoiceNumber = "FV/1\t/2026\n",
            IssueDate = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero),
            Seller = new Seller { Nip = "5908996669", Name = "Firma\tz tabulatorem" },
            Buyer = new Buyer { Identifier = new BuyerIdentifier { Type = BuyerIdentifierType.Nip, Value = "1111111111" }, Name = "Nabywca\nw dwóch wierszach" },
            GrossAmount = 1230.5m,
            Currency = "PLN",
        };

        string[] columns = KsefCli.Commands.ListCommand.ToTsv(invoice).Split('\t');

        Assert.Equal(
            ["5908996669-20261030-0123456789AB-CD", "FV/1 /2026 ", "2026-10-30", "5908996669", "Firma z tabulatorem", "1111111111", "Nabywca w dwóch wierszach", "1230.50", "PLN"],
            columns);
    }

    [Fact]
    public void ToTsv_ToleratesMissingBuyer()
    {
        InvoiceSummary invoice = new() { KsefNumber = "K", InvoiceNumber = "F", Currency = "PLN" };

        Assert.Equal(9, KsefCli.Commands.ListCommand.ToTsv(invoice).Split('\t').Length);
    }
}

public class CliErrorsTests
{
    [Fact]
    public void HttpTimeout_HasMessage_AndIsNotAnInterrupt()
    {
        TaskCanceledException timeout = new("The request was canceled due to the configured HttpClient.Timeout", new TimeoutException());

        Assert.Contains("przekroczono czas", CliErrors.Describe(timeout));
        Assert.False(CliErrors.IsInterrupt(timeout));
    }

    [Fact]
    public void UserCancellation_IsAnInterrupt()
    {
        Assert.Null(CliErrors.Describe(new OperationCanceledException()));
        Assert.True(CliErrors.IsInterrupt(new OperationCanceledException()));
        Assert.True(CliErrors.IsInterrupt(new TaskCanceledException()));
    }

    [Fact]
    public void KnownFailures_HaveMessages()
    {
        Assert.NotNull(CliErrors.Describe(new KSeF.Client.Core.Exceptions.KsefCircuitBreakerOpenException("otwarty")));
        Assert.NotNull(CliErrors.Describe(new HttpRequestException("brak sieci")));
        Assert.NotNull(CliErrors.Describe(new IOException("dysk pełny")));
        Assert.NotNull(CliErrors.Describe(new UnauthorizedAccessException("brak praw")));
        Assert.NotNull(CliErrors.Describe(new SessionException("brak sesji")));
    }

    [Fact]
    public void ProgramBugs_AreNotHidden()
    {
        Assert.Null(CliErrors.Describe(new NullReferenceException()));
        Assert.Null(CliErrors.Describe(new InvalidCastException()));
        Assert.False(CliErrors.IsInterrupt(new NullReferenceException()));
    }
}
