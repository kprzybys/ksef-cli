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

return await root.Parse(args).InvokeAsync();
