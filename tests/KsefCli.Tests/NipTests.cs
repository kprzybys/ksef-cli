namespace KsefCli.Tests;

public class NipTests
{
    [Theory]
    [InlineData("5908996669")]
    [InlineData("1111111111")]
    [InlineData("5260250995")] // Ministerstwo Finansów
    public void IsValid_AcceptsCorrectChecksum(string nip) => Assert.True(Nip.IsValid(nip));

    [Theory]
    [InlineData("5908996668")] // zła cyfra kontrolna
    [InlineData("1234567890")]
    [InlineData("123456789")] // za krótki
    [InlineData("12345678901")] // za długi
    [InlineData("590-899-66-69")] // z kreskami
    [InlineData("59O8996669")] // litera O zamiast zera
    [InlineData("")]
    [InlineData("1000000006")] // suma kontrolna dobra, ale wzorzec KSeF zabrania zer na pozycjach 2–3
    public void IsValid_RejectsMalformed(string nip) => Assert.False(Nip.IsValid(nip));

    [Fact]
    public void IsValid_RejectsChecksumTen()
    {
        // Suma modulo 11 równa 10 nie daje cyfry kontrolnej — takiego NIP-u nie wydaje się,
        // więc żadna ostatnia cyfra nie może go „naprawić”.
        string prefix = Enumerable.Range(123_000_000, 1000) // prefiks zgodny ze wzorcem KSeF — odrzuca tylko suma
            .Select(n => n.ToString())
            .First(p => p.Zip(new[] { 6, 5, 7, 2, 3, 4, 5, 6, 7 }, (c, w) => (c - '0') * w).Sum() % 11 == 10);

        Assert.All(Enumerable.Range(0, 10), d => Assert.False(Nip.IsValid(prefix + d)));
    }

    [Fact]
    public void RandomNip_FromTestsIsValid() =>
        Assert.All(Enumerable.Range(0, 100), _ => Assert.True(Nip.IsValid(TestData.RandomNip())));
}
