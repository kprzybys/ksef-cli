using System.Text.Json;
using KSeF.Client.Core.Exceptions;

namespace KsefCli;

/// <summary>
/// Ostatnia siatka bezpieczeństwa: błędy, których komenda nie obsłużyła sama, zamieniamy na jeden wiersz
/// `ksef: …` i kod 1 zamiast zrzutu stosu — a przekroczenie czasu HTTP na komunikat zamiast cichego wyjścia
/// (System.CommandLine traktuje każde OperationCanceledException jak Ctrl+C i nic nie wypisuje).
/// </summary>
public static class CliErrors
{
    /// <summary>Komunikat dla błędu, który znamy; null — błąd programu, niech poleci ze stosem.</summary>
    public static string? Describe(Exception ex) => ex switch
    {
        // HttpClient zgłasza swój limit czasu jako TaskCanceledException z TimeoutException w środku.
        TaskCanceledException { InnerException: TimeoutException } => "przekroczono czas oczekiwania na odpowiedź KSeF — spróbuj ponownie później.",
        KsefCircuitBreakerOpenException => $"KSeF chwilowo nie odpowiada poprawnie (wiele błędów z rzędu): {ex.Message} — spróbuj ponownie później.",
        KsefApiException or HttpRequestException or SessionException => ex.Message,
        IOException or UnauthorizedAccessException => ex.Message,
        JsonException => $"niepoprawne dane JSON: {ex.Message}",
        _ => null,
    };

    /// <summary>Przerwanie przez użytkownika (Ctrl+C), nie limit czasu.</summary>
    public static bool IsInterrupt(Exception ex) => ex is OperationCanceledException && Describe(ex) is null;
}
