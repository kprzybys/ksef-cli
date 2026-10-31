using System.CommandLine;
using KsefCli;
using KsefCli.Commands;

Option<string> envOption = new("--env", "-e")
{
    Description = $"Środowisko KSeF: test (piaskownica), demo (próba), prod (prawdziwe faktury); domyślnie z ${KsefEnvironment.EnvVariable}, a bez niej test",
    HelpName = string.Join('|', KsefEnvironment.Names),
    DefaultValueFactory = _ => KsefEnvironment.DefaultName,
    Recursive = true,
};
envOption.Validators.Add(result =>
{
    string name = result.GetValueOrDefault<string>();
    if (!KsefEnvironment.Names.Contains(name, StringComparer.Ordinal))
    {
        string source = result.Implicit ? $"{KsefEnvironment.EnvVariable}={name}" : name;
        result.AddError($"Nieznane środowisko: {source}. Dozwolone: {string.Join(", ", KsefEnvironment.Names)}.");
    }
});

RootCommand root = new("ksef — obsługa KSeF z wiersza poleceń (oficjalny klient MF)");
root.Options.Add(envOption);
root.SetAction(_ => FirstSteps.Write(Console.Out));
root.Subcommands.Add(StatusCommand.Create(envOption));
root.Subcommands.Add(ValidateCommand.Create());
root.Subcommands.Add(ShowCommand.Create(envOption));
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
