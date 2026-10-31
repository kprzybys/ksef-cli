using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using KSeF.Client.Http;

namespace KsefCli;

/// <summary>
/// JSON bez refleksji (Native AOT): metadane typów z kontekstów generowanych przy kompilacji.
/// Każdy typ serializowany do/z JSON — odpowiedzi i żądania MF w nowych komendach, nasze rekordy
/// dla --json — musi być w jednym z kontekstów niżej, inaczej serializacja rzuca NotSupportedException
/// (także w JIT, więc braki wychodzą w testach). Szukanie braków: KSEF_JSON_DISCOVER=plik (tylko JIT)
/// dopisuje do pliku typy spoza kontekstów, a serializuje je refleksją.
/// </summary>
internal static class KsefJson
{
    private static readonly string? DiscoverPath =
        RuntimeFeature.IsDynamicCodeSupported ? Environment.GetEnvironmentVariable("KSEF_JSON_DISCOVER") : null;

    /// <summary>
    /// Biblioteka MF serializuje przez prywatne JsonUtil._settings, które AddKSeFClient tworzy od nowa —
    /// wywoływać po każdej rejestracji klienta (KsefServices.AddKsefClient).
    /// </summary>
    public static void InstallInKsefClient()
    {
        FieldInfo field = typeof(JsonUtil).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("KSeF.Client: brak pola JsonUtil._settings — zmieniła się biblioteka MF, popraw KsefJson.");
        JsonSerializerOptions current = (JsonSerializerOptions)field.GetValue(null)!;
        JsonSerializerOptions options = Configure(new JsonSerializerOptions(current), MfJsonContext.Default);
        // Pole authenticationMethod MF wycofuje (zastępuje je authenticationMethodInfo), a nowe metody logowania
        // dochodzą bez zmiany biblioteki: nieznana wartość nie może przerwać logowania — ksef jej nie używa.
        options.Converters.Add(new LenientEnumConverter<KSeF.Client.Core.Models.Sessions.ActiveSessions.AuthenticationMethodEnum>());
        field.SetValue(null, options);
    }

    /// <summary>Enum jako tekst; wartość spoza enuma (nowa w API) daje wartość domyślną zamiast wyjątku.</summary>
    private sealed class LenientEnumConverter<T> : JsonConverter<T>
        where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String && Enum.TryParse(reader.GetString(), ignoreCase: true, out T value) ? value : default;

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    public static JsonSerializerOptions Configure(JsonSerializerOptions options, IJsonTypeInfoResolver context)
    {
        // Niegeneryczny konwerter enumów generuje kod w trakcie działania; enumy jako tekst dają konteksty.
        for (int i = options.Converters.Count - 1; i >= 0; i--)
        {
            if (options.Converters[i] is JsonStringEnumConverter)
            {
                options.Converters.RemoveAt(i);
            }
        }
        if (RuntimeFeature.IsDynamicCodeSupported && DiscoverPath is { Length: > 0 } path)
        {
            options.TypeInfoResolver = JsonTypeInfoResolver.Combine(context, new MissReporter(path));
            options.Converters.Add(new JsonStringEnumConverter());
        }
        else
        {
            options.TypeInfoResolver = context;
        }
        return options;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Tylko tryb odkrywania w JIT.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Tylko tryb odkrywania w JIT.")]
    private sealed class MissReporter(string path) : IJsonTypeInfoResolver
    {
        private readonly DefaultJsonTypeInfoResolver reflection = new();

        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
        {
            File.AppendAllText(path, type + Environment.NewLine);
            return reflection.GetTypeInfo(type, options);
        }
    }
}

/// <summary>Żądania i odpowiedzi API KSeF (biblioteka MF; nazwy właściwości jak w C#, bez rozróżniania wielkości liter).</summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(KSeF.Client.Core.Models.Lighthouse.KsefStatusResponse))]
[JsonSerializable(typeof(ICollection<KSeF.Client.Core.Models.Certificates.PemCertificateInfo>))]
[JsonSerializable(typeof(KSeF.Client.Core.Models.Authorization.AuthenticationChallengeResponse))]
[JsonSerializable(typeof(KSeF.Client.Core.Models.Authorization.SignatureResponse))]
[JsonSerializable(typeof(KSeF.Client.Core.Models.AuthStatus))]
[JsonSerializable(typeof(KSeF.Client.Core.Models.Authorization.AuthenticationOperationStatusResponse))]
[JsonSerializable(typeof(KSeF.Client.Core.Models.Authorization.RefreshTokenResponse))]
// login --token-file: żądanie z zaszyfrowanym tokenem KSeF.
[JsonSerializable(typeof(KSeF.Client.Core.Models.Authorization.AuthenticationKsefTokenRequest))]
// Generowanie i unieważnianie tokenów KSeF (dziś tylko testy E2E logowania tokenem).
[JsonSerializable(typeof(KSeF.Client.Core.Models.Authorization.KsefTokenRequest))]
[JsonSerializable(typeof(KSeF.Client.Core.Models.Authorization.KsefTokenResponse))]
[JsonSerializable(typeof(KSeF.Client.Core.Models.Authorization.AuthenticationKsefToken))]
[JsonSerializable(typeof(KSeF.Client.Core.Models.Invoices.InvoiceQueryFilters))]
[JsonSerializable(typeof(KSeF.Client.Core.Models.Invoices.PagedInvoiceResponse))]
// send: sesja interaktywna i status faktury.
[JsonSerializable(typeof(KSeF.Client.Core.Models.Sessions.OnlineSession.OpenOnlineSessionRequest))]
[JsonSerializable(typeof(KSeF.Client.Core.Models.Sessions.OnlineSession.OpenOnlineSessionResponse))]
[JsonSerializable(typeof(KSeF.Client.Core.Models.Sessions.SendInvoiceRequest))]
[JsonSerializable(typeof(KSeF.Client.Core.Models.Sessions.OnlineSession.SendInvoiceResponse))]
[JsonSerializable(typeof(KSeF.Client.Core.Models.Sessions.SessionInvoice))]
[JsonSerializable(typeof(KSeF.Client.Core.Models.Sessions.SessionStatusResponse))]
// Treść błędów API (RestClient.ErrorHandling).
[JsonSerializable(typeof(KSeF.Client.Core.Exceptions.ApiErrorResponse))]
[JsonSerializable(typeof(KSeF.Client.Core.Exceptions.BadRequestProblemDetails))]
[JsonSerializable(typeof(KSeF.Client.Core.Exceptions.UnauthorizedProblemDetails))]
[JsonSerializable(typeof(KSeF.Client.Core.Exceptions.ForbiddenProblemDetails))]
[JsonSerializable(typeof(KSeF.Client.Core.Exceptions.GoneProblemDetails))]
[JsonSerializable(typeof(KSeF.Client.Core.Exceptions.TooManyRequestsProblemDetails))]
[JsonSerializable(typeof(KSeF.Client.Core.Exceptions.TooManyRequestsErrorResponse))]
[JsonSerializable(typeof(KSeF.Client.Core.Exceptions.ProblemDetails))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class MfJsonContext : JsonSerializerContext;

/// <summary>Wynik komend z --json (camelCase, enumy jako tekst).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(Commands.StatusCommand.StatusResult))]
[JsonSerializable(typeof(List<Commands.ValidateCommand.FileResult>))]
[JsonSerializable(typeof(List<Commands.SessionCommands.SessionInfo>))]
[JsonSerializable(typeof(List<Commands.SendCommand.SendResult>))]
[JsonSerializable(typeof(IReadOnlyList<KSeF.Client.Core.Models.Invoices.InvoiceSummary>))]
internal sealed partial class CliJsonContext : JsonSerializerContext;

/// <summary>Pliki sesji w $XDG_RUNTIME_DIR/ksef.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(KsefSession))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;
