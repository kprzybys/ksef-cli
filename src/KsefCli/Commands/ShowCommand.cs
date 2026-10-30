using System.CommandLine;
using Ksef.Fa3;

namespace KsefCli.Commands;

/// <summary>`ksef show` — podgląd faktury FA(3) w HTML oficjalnym arkuszem MF, offline.</summary>
public static class ShowCommand
{
    public static Command Create()
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

        Command command = new("show", "Podgląd faktury FA(3) w HTML na stdout (oficjalny arkusz MF, bez sieci)");
        command.Arguments.Add(fileArgument);
        command.Options.Add(outputOption);
        command.SetAction(parseResult => Run(
            parseResult.GetRequiredValue(fileArgument),
            parseResult.GetRequiredValue(outputOption)));
        return command;
    }

    private static int Run(string file, string output)
    {
        try
        {
            using MemoryStream xml = new(CliIo.ReadAllBytes(file));
            using MemoryStream html = new();
            Fa3Html.Render(xml, html);
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
