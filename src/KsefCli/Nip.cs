using System.CommandLine;
using KSeF.Client.Validation;

namespace KsefCli;

/// <summary>
/// Numer NIP w rozumieniu KSeF: wzorzec ze schematów KSeF (m.in. cyfry 2–3 nie mogą być obie zerami)
/// i suma kontrolna — oba sprawdzenia z biblioteki MF, żeby odrzucić to samo co ona, zanim wyśle żądanie.
/// </summary>
public static class Nip
{
    public static bool IsValid(string nip) =>
        RegexPatterns.Nip.IsMatch(nip) && IdentifierValidators.IsValidNip(nip);

    public static Option<string> Option(string description)
    {
        Option<string> option = new("--nip") { Description = description };
        option.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<string>() is { } nip && !IsValid(nip))
            {
                result.AddError($"Niepoprawny NIP: {nip} (10 cyfr z poprawną sumą kontrolną).");
            }
        });
        return option;
    }
}
