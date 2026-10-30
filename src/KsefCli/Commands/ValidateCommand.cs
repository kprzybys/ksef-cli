using System.CommandLine;
using Ksef.Fa3;

namespace KsefCli.Commands;

/// <summary>`ksef validate` — kontrola pliku faktury przed wysyłką: wymagania KSeF + schemat FA(3), offline.</summary>
public static class ValidateCommand
{
    public static Command Create()
    {
        Argument<FileInfo[]> filesArgument = new("pliki")
        {
            Description = "Pliki XML faktur FA(3)",
            Arity = ArgumentArity.OneOrMore,
        };
        filesArgument.AcceptExistingOnly();

        Command command = new("validate", "Sprawdza plik(i) XML faktury: wymagania KSeF (kodowanie, znaki, rozmiar) i schemat FA(3), bez sieci");
        command.Arguments.Add(filesArgument);
        command.SetAction(parseResult => Run(parseResult.GetRequiredValue(filesArgument)));
        return command;
    }

    private static int Run(FileInfo[] files)
    {
        int invalid = 0;
        foreach (FileInfo file in files)
        {
            IReadOnlyList<Fa3ValidationError> errors = Fa3Validator.Validate(File.ReadAllBytes(file.FullName));

            if (errors.Count == 0)
            {
                Console.WriteLine($"OK    {file.Name}");
                continue;
            }

            invalid++;
            Console.WriteLine($"BŁĄD  {file.Name}");
            foreach (Fa3ValidationError error in errors)
            {
                Console.WriteLine($"      {error}");
            }
        }
        return invalid == 0 ? 0 : 1;
    }
}
