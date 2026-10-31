namespace KsefCli;

/// <summary>
/// `ksef` bez komendy: zamiast suchej listy komend — trzy środowiska po ludzku i jak zacząć. Nowy użytkownik
/// zwykle nie czyta dokumentacji, a wybór środowiska decyduje, czy faktura jest prawdziwa.
/// </summary>
public static class FirstSteps
{
    public static int Write(TextWriter output)
    {
        output.Write($"""
            ksef — KSeF Ministerstwa Finansów z wiersza poleceń.

            Środowisko wybierasz w każdej komendzie (-e) albo raz, zmienną {KsefEnvironment.EnvVariable}:

              ksef -e prod …   {KsefEnvironment.Describe("prod")} Twojej firmy (wysłana = wystawiona)
              ksef -e demo …   {KsefEnvironment.Describe("demo")} (demo)
              ksef …           {KsefEnvironment.Describe("test")} (test, domyślnie)

            Pierwsze kroki z prawdziwą firmą (token wygenerowany w Aplikacji Podatnika KSeF):
              ksef -e prod login --nip <NIP> --token-file token.txt
              ksef -e prod list                  # faktury otrzymane w tym miesiącu

            Bez prawdziwej firmy, w piaskownicy:
              ksef cert new --nip 5908996669 -o test-cert.pem
              ksef login --nip 5908996669 --cert test-cert.pem

            Wszystkie komendy: ksef --help

            """);
        return 0;
    }
}
