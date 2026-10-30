using KSeF.Client.Core.Exceptions;

namespace KsefCli;

/// <summary>
/// Limit żądań KSeF (HTTP 429, np. „8 żądań na sekundę”): biblioteka MF zgłasza go z zalecanym czasem
/// z nagłówka Retry-After, ale sama nie ponawia. Komendy wysyłające serie żądań (list w oknach, get wielu
/// faktur, odpytywanie statusów) czekają wskazany czas i ponawiają. Prób mniej niż 5, bo po 5 kolejnych
/// błędach bezpiecznik biblioteki (circuit breaker) odcina wszystkie żądania. Limitów długich (np. „20 zapytań
/// o metadane na godzinę”) nie przeczekujemy — od razu oddajemy komunikat KSeF z czasem do ponowienia.
/// </summary>
public static class KsefRetry
{
    public const int MaxAttempts = 4;

    private static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(1);

    public static async Task<T> OnRateLimitAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        delay ??= Task.Delay;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (KsefRateLimitException ex) when (attempt < MaxAttempts && ex.RecommendedDelay <= MaxDelay)
            {
                await delay(ex.RecommendedDelay < MinDelay ? MinDelay : ex.RecommendedDelay, cancellationToken);
            }
        }
    }

    public static Task OnRateLimitAsync(Func<Task> call, CancellationToken cancellationToken) =>
        OnRateLimitAsync(async () => { await call(); return true; }, cancellationToken);
}
