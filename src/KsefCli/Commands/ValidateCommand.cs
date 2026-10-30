using System.CommandLine;
using System.Xml.Schema;
using Ksef.Fa3;

namespace KsefCli.Commands;

/// <summary>
/// `ksef validate` — kontrola pliku faktury przed wysyłką: wymagania KSeF + schemat FA(3), offline.
/// Przy sukcesie milczy; błędy idą na stderr w formacie `plik:wiersz:kolumna: ...` (jak kompilatory).
/// </summary>
public static class ValidateCommand
{
    // internal: typ wyniku --json musi być widoczny dla CliJsonContext.
    internal sealed record FileResult(string File, bool Valid, IReadOnlyList<Fa3ValidationError> Errors);

    public static Command Create()
    {
        Argument<string[]> filesArgument = new("pliki")
        {
            Description = "Pliki XML faktur FA(3); `-` albo brak = stdin",
            Arity = ArgumentArity.ZeroOrMore,
        };
        Option<bool> verboseOption = new("--verbose", "-v")
        {
            Description = "Wypisz też poprawne pliki",
        };
        Option<bool> jsonOption = CliIo.JsonOption();

        Command command = new("validate", "Sprawdza plik(i) XML faktury: wymagania KSeF (kodowanie, znaki, rozmiar) i schemat FA(3), bez sieci");
        command.Arguments.Add(filesArgument);
        command.Options.Add(verboseOption);
        command.Options.Add(jsonOption);
        command.SetAction(parseResult => Run(
            parseResult.GetValue(filesArgument) is { Length: > 0 } files ? files : [CliIo.StdStream],
            parseResult.GetValue(verboseOption),
            parseResult.GetValue(jsonOption)));
        return command;
    }

    private static int Run(string[] files, bool verbose, bool json)
    {
        List<FileResult> results = [];
        bool readFailed = false;
        foreach (string file in files)
        {
            byte[] xml;
            try
            {
                xml = CliIo.ReadAllBytes(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CliIo.Error($"{CliIo.DisplayName(file)}: {ex.Message}");
                readFailed = true;
                continue;
            }

            IReadOnlyList<Fa3ValidationError> errors = Fa3Validator.Validate(xml);
            results.Add(new FileResult(CliIo.DisplayName(file), errors.Count == 0, errors));
        }

        if (json)
        {
            CliIo.WriteJson(results);
        }
        else
        {
            foreach (FileResult result in results)
            {
                if (result.Valid && verbose)
                {
                    Console.Out.WriteLine($"{result.File}: OK");
                }
                foreach (Fa3ValidationError error in result.Errors)
                {
                    string severity = error.Severity == XmlSeverityType.Error ? "błąd" : "ostrzeżenie";
                    Console.Error.WriteLine($"{result.File}:{error.Line}:{error.Column}: {error.Source} {severity}: {error.Message}");
                }
            }
        }

        return readFailed || results.Any(r => !r.Valid) ? 1 : 0;
    }
}
