using System.CommandLine;
using System.Diagnostics;
using Ksef.Fa3;

namespace KsefCli.Commands;

/// <summary>`ksef show` — podgląd faktury FA(3) w HTML oficjalnym arkuszem MF, offline.</summary>
public static class ShowCommand
{
    public static Command Create()
    {
        Argument<FileInfo> fileArgument = new("plik") { Description = "Plik XML faktury FA(3)" };
        fileArgument.AcceptExistingOnly();

        Option<FileInfo?> outputOption = new("--output", "-o")
        {
            Description = "Plik HTML (domyślnie obok XML, z rozszerzeniem .html)",
        };
        Option<bool> openOption = new("--open")
        {
            Description = "Otwórz wynik w domyślnej przeglądarce (xdg-open)",
        };

        Command command = new("show", "Podgląd faktury FA(3) w HTML (oficjalny arkusz MF, bez sieci)");
        command.Arguments.Add(fileArgument);
        command.Options.Add(outputOption);
        command.Options.Add(openOption);
        command.SetAction(parseResult => Run(
            parseResult.GetRequiredValue(fileArgument),
            parseResult.GetValue(outputOption),
            parseResult.GetValue(openOption)));
        return command;
    }

    private static int Run(FileInfo file, FileInfo? output, bool open)
    {
        string htmlPath = output?.FullName ?? Path.ChangeExtension(file.FullName, ".html");
        try
        {
            using FileStream xml = file.OpenRead();
            using FileStream html = File.Create(htmlPath);
            Fa3Html.Render(xml, html);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or System.Xml.Xsl.XsltException)
        {
            File.Delete(htmlPath);
            Console.Error.WriteLine($"Nie udało się przetworzyć {file.Name}: {ex.Message}");
            return 1;
        }

        Console.WriteLine(htmlPath);
        if (open)
        {
            Process.Start(new ProcessStartInfo("xdg-open", [htmlPath]) { UseShellExecute = false });
        }
        return 0;
    }
}
