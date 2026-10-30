using System.Text;
using System.Xml;
using KSeF.Client.Core.Exceptions;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Models.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace KsefCli.Commands;

/// <summary>
/// Logowanie podpisem złożonym innym programem (Profil Zaufany, aplikacja do podpisu kwalifikowanego):
/// `ksef login --request` wypisuje niepodpisane żądanie z wyzwaniem, `ksef login --signed` wysyła je
/// po podpisaniu. Między krokami nic nie zapisujemy — wyzwanie jest w samym żądaniu. Wyzwanie KSeF
/// ważne jest krótko (minuty), więc podpisać trzeba od razu.
/// </summary>
public static partial class LoginCommand
{
    private const string XmlDsigNamespace = "http://www.w3.org/2000/09/xmldsig#";

    private static async Task<int> WriteRequestAsync(KsefEnvironment env, string nip, CancellationToken cancellationToken)
    {
        await using ServiceProvider provider = KsefServices.Create(env);
        using IServiceScope scope = provider.CreateScope();
        IAuthorizationClient authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationClient>();
        try
        {
            string xml = await NewRequestXmlAsync(authorization, nip, cancellationToken);
            CliIo.WriteAllBytes(CliIo.StdStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(xml));
            return 0;
        }
        catch (Exception ex) when (ex is KsefApiException or HttpRequestException or ArgumentException)
        {
            CliIo.Error($"żądanie logowania NIP {nip} ({env.Name}): {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunWithSignedRequestAsync(KsefEnvironment env, string nip, string signedPath, CancellationToken cancellationToken)
    {
        if (!CanSaveSession())
        {
            return 1;
        }

        string signedXml;
        try
        {
            // Bajty bez zmian (poza BOM): każda zmiana treści unieważniłaby podpis.
            byte[] bytes = CliIo.ReadAllBytes(signedPath);
            signedXml = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(
                bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? bytes[3..] : bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CliIo.Error($"{CliIo.DisplayName(signedPath)}: {ex.Message}");
            return 1;
        }
        if (CheckSignedRequest(signedXml, nip) is { } problem)
        {
            CliIo.Error($"{CliIo.DisplayName(signedPath)}: {problem}");
            return 1;
        }

        await using ServiceProvider provider = KsefServices.Create(env);
        using IServiceScope scope = provider.CreateScope();
        IAuthorizationClient authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationClient>();
        try
        {
            SignatureResponse submission = await authorization.SubmitXadesAuthRequestAsync(signedXml, cancellationToken: cancellationToken);
            SessionStore.Save(await CompleteAsync(authorization, env, nip, submission, cancellationToken));
            return 0;
        }
        catch (Exception ex) when (ex is KsefApiException or HttpRequestException or AuthenticationFailedException or SessionException or ArgumentException)
        {
            CliIo.Error($"logowanie NIP {nip} ({env.Name}) podpisanym żądaniem nieudane: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Kontrola przed wysyłką, żeby pomyłkę (inny plik, niepodpisane żądanie, inny NIP) zgłosić od razu,
    /// a nie jako ogólny błąd KSeF. Samego podpisu nie weryfikujemy — to robi KSeF.
    /// </summary>
    public static string? CheckSignedRequest(string xml, string nip)
    {
        XmlDocument document = new() { XmlResolver = null };
        try
        {
            using XmlReader reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document.Load(reader);
        }
        catch (XmlException ex)
        {
            return $"to nie jest poprawny XML: {ex.Message}";
        }

        if (document.DocumentElement is not { LocalName: "AuthTokenRequest" })
        {
            return "to nie jest żądanie logowania KSeF (oczekiwano AuthTokenRequest z `ksef login --request`).";
        }
        string? contextNip = document.SelectSingleNode("/*/*[local-name()='ContextIdentifier']/*[local-name()='Nip']")?.InnerText.Trim();
        if (!string.Equals(contextNip, nip, StringComparison.Ordinal))
        {
            return $"żądanie jest dla NIP {contextNip ?? "(brak)"}, a logujesz się na {nip}.";
        }
        if (document.GetElementsByTagName("Signature", XmlDsigNamespace).Count == 0)
        {
            return "żądanie nie jest podpisane (brak ds:Signature) — podpisz je podpisem XAdES w postaci otaczanej (enveloped).";
        }
        return null;
    }
}
