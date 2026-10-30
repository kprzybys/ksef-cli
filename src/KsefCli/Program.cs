using System.CommandLine;
using KsefCli;
using KsefCli.Commands;

Option<string> envOption = new("--env", "-e")
{
    Description = "Środowisko KSeF: test, demo, prod",
    DefaultValueFactory = _ => "test",
    Recursive = true,
};
envOption.AcceptOnlyFromAmong(KsefEnvironment.Names);

RootCommand root = new("ksef — obsługa KSeF z wiersza poleceń (oficjalny klient MF)");
root.Options.Add(envOption);
root.Subcommands.Add(StatusCommand.Create(envOption));
root.Subcommands.Add(ValidateCommand.Create());
root.Subcommands.Add(ShowCommand.Create());
root.Subcommands.Add(CertCommand.Create());
root.Subcommands.Add(LoginCommand.Create(envOption));
root.Subcommands.Add(SessionCommands.CreateLogout(envOption));
root.Subcommands.Add(SessionCommands.CreateList(envOption));
root.Subcommands.Add(ListCommand.Create(envOption));
root.Subcommands.Add(SendCommand.Create(envOption));
root.Subcommands.Add(GetCommand.Create(envOption));
root.Subcommands.Add(UpoCommand.Create(envOption));

// Własna obsługa błędów zamiast domyślnej: zrzut stosu tylko dla błędów programu (CliErrors).
InvocationConfiguration invocation = new() { EnableDefaultExceptionHandler = false };
try
{
    return await root.Parse(args).InvokeAsync(invocation);
}
catch (Exception ex) when (CliErrors.Describe(ex) is { } message)
{
    CliIo.Error(message);
    return 1;
}
catch (Exception ex) when (CliErrors.IsInterrupt(ex))
{
    return 130;
}
