using System.CommandLine;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using KSeF.Client.Api.Builders.X509Certificates;

namespace KsefCli.Commands;

/// <summary>
/// `ksef cert new` — certyfikat podpisany samym sobą do logowania na środowisku TEST
/// (KSeF TEST przyjmuje takie certyfikaty; DEMO i PROD wymagają kwalifikowanego lub certyfikatu KSeF).
/// Wynik: PEM z certyfikatem i kluczem prywatnym.
/// </summary>
public static class CertCommand
{
    public static Command Create()
    {
        Option<string> nipOption = Nip.Option("NIP, na który wystawić certyfikat");
        nipOption.Required = true;
        Option<bool> sealOption = new("--seal")
        {
            Description = "Pieczęć firmy (identyfikator VATPL-NIP) zamiast certyfikatu osoby (TINPL-NIP)",
        };
        Option<string> outputOption = new("--output", "-o")
        {
            Description = "Plik PEM (tworzony z prawami 0600, nie nadpisuje istniejącego); `-` albo brak = stdout",
            DefaultValueFactory = _ => CliIo.StdStream,
        };

        Command newCommand = new("new", "Nowy certyfikat testowy (podpisany samym sobą) jako PEM z kluczem prywatnym — tylko dla środowiska TEST");
        newCommand.Options.Add(nipOption);
        newCommand.Options.Add(sealOption);
        newCommand.Options.Add(outputOption);
        newCommand.SetAction(parseResult => RunNew(
            parseResult.GetRequiredValue(nipOption),
            parseResult.GetValue(sealOption),
            parseResult.GetRequiredValue(outputOption)));

        Command command = new("cert", "Certyfikaty do logowania w KSeF");
        command.Subcommands.Add(newCommand);
        return command;
    }

    private static int RunNew(string nip, bool seal, string output)
    {
        if (output == CliIo.StdStream && !Console.IsOutputRedirected)
        {
            CliIo.Error("nie wypiszę klucza prywatnego na terminal — przekieruj wynik albo użyj -o <plik>.");
            return 1;
        }

        using X509Certificate2 certificate = seal
            ? SelfSignedCertificateForSealBuilder.Create()
                .WithOrganizationName("Firma testowa KSeF")
                .WithOrganizationIdentifier($"VATPL-{nip}")
                .WithCommonName("Firma testowa KSeF")
                .Build()
            : SelfSignedCertificateForSignatureBuilder.Create()
                .WithGivenName("Test")
                .WithSurname("KSeF")
                .WithSerialNumber($"TINPL-{nip}")
                .WithCommonName("Test KSeF")
                .Build();

        using RSA key = certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException("Certyfikat bez klucza RSA.");
        byte[] pem = Encoding.ASCII.GetBytes(certificate.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem() + "\n");

        if (output == CliIo.StdStream)
        {
            CliIo.WriteAllBytes(output, pem);
            return 0;
        }

        try
        {
            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };
            using FileStream file = new(output, options);
            file.Write(pem);
            return 0;
        }
        catch (IOException ex)
        {
            CliIo.Error($"{output}: {ex.Message}");
            return 1;
        }
    }
}
