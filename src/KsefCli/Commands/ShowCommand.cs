using System.CommandLine;
using Ksef.Fa3;

namespace KsefCli.Commands;

/// <summary>
/// `ksef show` — podgląd faktury FA(3) w HTML oficjalnym arkuszem MF, offline, z kodem QR weryfikującym
/// fakturę (KOD I), wymaganym na fakturze przekazanej poza KSeF.
/// </summary>
public static class ShowCommand
{
    public static Command Create(Option<string> envOption)
    {
        Argument<string> fileArgument = new("plik")
        {
            Description = "Plik XML faktury FA(3); `-` albo brak = stdin",
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => CliIo.StdStream,
        };

        Option<string> outputOption = new("--output", "-o")
        {
            Description = "Plik HTML; `-` albo brak = stdout",
            DefaultValueFactory = _ => CliIo.StdStream,
        };

        Option<string?> ksefNumberOption = new("--ksef-number", "-k")
        {
            Description = "Numer KSeF faktury — podpis pod kodem QR; bez niego „OFFLINE” (faktura jeszcze bez numeru)",
        };

        Option<bool> noQrOption = new("--no-qr")
        {
            Description = "Bez kodu QR weryfikującego fakturę (tylko podgląd treści)",
        };

        Command command = new("show", "Podgląd faktury FA(3) w HTML na stdout (oficjalny arkusz MF, kod QR KSeF, bez sieci)");
        command.Arguments.Add(fileArgument);
        command.Options.Add(outputOption);
        command.Options.Add(ksefNumberOption);
        command.Options.Add(noQrOption);
        command.SetAction(parseResult => Run(
            parseResult.GetRequiredValue(fileArgument),
            parseResult.GetRequiredValue(outputOption),
            parseResult.GetValue(noQrOption) ? null : KsefEnvironment.Parse(parseResult.GetRequiredValue(envOption)),
            parseResult.GetValue(ksefNumberOption)));
        return command;
    }

    private static int Run(string file, string output, KsefEnvironment? qrEnvironment, string? ksefNumber)
    {
        if (ksefNumber is not null && !InvoiceQr.IsKsefNumber(ksefNumber))
        {
            CliIo.Error($"niepoprawny numer KSeF: {ksefNumber}");
            return 1;
        }
        try
        {
            byte[] xml = CliIo.ReadAllBytes(file);
            string? qr = null;
            if (qrEnvironment is not null)
            {
                qr = InvoiceQr.HtmlBlock(InvoiceQr.Url(qrEnvironment, xml), ksefNumber ?? "OFFLINE", qrEnvironment);
            }
            using MemoryStream html = new();
            Fa3Html.Render(new MemoryStream(xml), html, qr);
            CliIo.WriteAllBytes(output, html.ToArray());
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Xml.XmlException or System.Xml.Xsl.XsltException)
        {
            CliIo.Error($"{CliIo.DisplayName(file)}: {ex.Message}");
            return 1;
        }
    }
}
