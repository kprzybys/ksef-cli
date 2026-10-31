using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using KSeF.Client.Api.Services;

namespace KsefCli.Commands;

/// <summary>
/// Klucz prywatny, który nie opuszcza karty, tokenu ani TPM (`ksef login --cert cert.pem --key pkcs11:…`).
/// Podpis XAdES robi OpenSSL systemu przez moduł PKCS#11: engine `pkcs11` (libp11) albo provider `pkcs11`
/// (pkcs11-provider). Domyślny moduł obu to p11-kit, który widzi wszystkie zarejestrowane karty (OpenSC, TPM, SoftHSM).
/// Moduły piszą wprost na fd 2 — libp11 przy błędzie także URI z PIN-em — więc każda operacja na kluczu
/// (otwarcie, próbny podpis, podpis żądania) idzie z przechwyconym stderr: sukces cichy, błąd bez PIN-u.
/// </summary>
public static partial class LoginCommand
{
    // pkcs11-provider na OpenSSL 3.0 zakleszcza się z modułami, które same używają OpenSSL (np. SoftHSM),
    // dlatego przed 3.2 domyślnie engine. Wartość z SafeEvpPKeyHandle.OpenSslVersion: 0xMNN00PP0.
    private const long FirstProviderDefaultVersion = 0x3020_0000;

    // OpenSSL 4 usunął engine'y (ENGINE API) — zostaje tylko provider.
    private const long FirstVersionWithoutEngines = 0x4000_0000;

    private sealed record HardwareKey(string Uri, string? PinFile, string? Via);

    /// <summary>Certyfikat z kluczem z urządzenia i PIN — do maskowania komunikatów przy kolejnych operacjach.</summary>
    private sealed record AttachedKey(X509Certificate2 Certificate, string? Pin);

    /// <summary>Błąd operacji na kluczu z urządzenia; komunikat już bez PIN-u, z diagnostyką modułu.</summary>
    private sealed class HardwareKeyException(string message) : Exception(message);

    /// <summary>PIN podaje się tylko przez --pin-file, nie w URI klucza (trafiłby do komunikatów i historii powłoki).</summary>
    private static bool HasPinInUri(string uri) =>
        uri.Contains("pin-value=", StringComparison.OrdinalIgnoreCase) || uri.Contains("pin-source=", StringComparison.OrdinalIgnoreCase);

    private static bool TryAttachHardwareKey(X509Certificate2 certificate, HardwareKey key, [NotNullWhen(true)] out AttachedKey? attached)
    {
        attached = null;
        string? pin = null;
        if (key.PinFile is { } pinFile)
        {
            try
            {
                // Pierwszy niepusty wiersz bez końca wiersza — spacje należą do PIN-u.
                pin = Encoding.UTF8.GetString(CliIo.ReadAllBytes(pinFile))
                    .Split('\n')
                    .Select(line => line.TrimEnd('\r'))
                    .FirstOrDefault(line => line.Length > 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                CliIo.Error($"{CliIo.DisplayName(pinFile)}: nie udało się wczytać PIN-u: {ex.Message}");
                return false;
            }
            if (pin is null)
            {
                CliIo.Error($"{CliIo.DisplayName(pinFile)}: brak PIN-u.");
                return false;
            }
        }

        bool viaProvider = key.Via is { } via
            ? string.Equals(via, "provider", StringComparison.Ordinal)
            : SafeEvpPKeyHandle.OpenSslVersion >= FirstProviderDefaultVersion;
        if (!viaProvider && SafeEvpPKeyHandle.OpenSslVersion >= FirstVersionWithoutEngines)
        {
            CliIo.Error($"{key.Uri}: OpenSSL {SafeEvpPKeyHandle.OpenSslVersion >> 28} nie ma engine'ów (usunięte od OpenSSL 4) — "
                + "użyj --key-via provider albo pomiń --key-via (pakiet pkcs11-provider).");
            return false;
        }
        // Dostępność providera przed pierwszą próbą, nie po niej — bez dodatkowej inicjalizacji modułu po błędzie PIN-u.
        if (viaProvider && !OpenSslProvider.IsAvailable("pkcs11"))
        {
            CliIo.Error($"{key.Uri}: OpenSSL nie ma providera pkcs11 (albo nie udało się go uruchomić) — zainstaluj pkcs11-provider "
                + "albo użyj --key-via engine (Debian/Ubuntu: libengine-pkcs11-openssl).");
            return false;
        }

        // PIN tylko w pamięci, jako atrybut URI (RFC 7512) — nigdy w argumentach procesu ani w komunikatach.
        string uri = pin is null
            ? key.Uri
            : $"{key.Uri}{(key.Uri.Contains('?', StringComparison.Ordinal) ? '&' : '?')}pin-value={Uri.EscapeDataString(pin)}";
        try
        {
            X509Certificate2 withKey = WithCapturedStderr(pin, () =>
            {
                using SafeEvpPKeyHandle handle = viaProvider
                    ? SafeEvpPKeyHandle.OpenKeyFromProvider("pkcs11", uri)
                    : SafeEvpPKeyHandle.OpenPrivateKeyFromEngine("pkcs11", uri);
                X509Certificate2 copy = AttachKey(certificate, handle);
                try
                {
                    // pkcs11-provider loguje się do tokenu leniwie, przy pierwszym podpisie — zły PIN ma wyjść tutaj,
                    // przed połączeniem z KSeF, a nie dopiero przy podpisie żądania.
                    ProbeSignature(copy);
                    return copy;
                }
                catch
                {
                    copy.Dispose();
                    throw;
                }
            });
            attached = new AttachedKey(withKey, pin);
            return true;
        }
        catch (HardwareKeyException ex)
        {
            string hint = !viaProvider && ex.Message.Contains("no such engine", StringComparison.Ordinal)
                ? "OpenSSL nie ma engine'u pkcs11 — zainstaluj libp11 (Debian/Ubuntu: libengine-pkcs11-openssl, "
                    + "Fedora/RHEL 9: openssl-pkcs11) albo użyj --key-via provider (pakiet pkcs11-provider)"
                : $"nie udało się użyć klucza przez OpenSSL {(viaProvider ? "provider" : "engine")} pkcs11 "
                    + "(zły PIN, brak karty albo klucz nie pasuje do certyfikatu?)";
            CliIo.Error($"{key.Uri}: {hint}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Podpis żądania logowania kluczem z urządzenia — też z przechwyconym stderr modułu.</summary>
    private static string SignWithHardwareKey(string xml, AttachedKey key) =>
        WithCapturedStderr(key.Pin, () => SignatureService.Sign(xml, key.Certificate));

    private static X509Certificate2 AttachKey(X509Certificate2 certificate, SafeEvpPKeyHandle handle)
    {
        using RSA? rsaPublic = certificate.GetRSAPublicKey();
        if (rsaPublic is not null)
        {
            using RSAOpenSsl rsa = new(handle);
            return certificate.CopyWithPrivateKey(rsa);
        }
        using ECDsaOpenSsl ecdsa = new(handle);
        return certificate.CopyWithPrivateKey(ecdsa);
    }

    private static void ProbeSignature(X509Certificate2 certificate)
    {
        byte[] probe = "ksef: próba klucza"u8.ToArray();
        using RSA? rsa = certificate.GetRSAPrivateKey();
        if (rsa is not null)
        {
            _ = rsa.SignData(probe, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return;
        }
        using ECDsa? ecdsa = certificate.GetECDsaPrivateKey();
        _ = ecdsa?.SignData(probe, HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// Operacja z natywnym stderr przechwyconym do pamięci. Błąd — także niemożność przechwycenia (wtedy nie ryzykujemy
    /// wypisania PIN-u i przerywamy) — wraca jako <see cref="HardwareKeyException"/> z zamaskowaną diagnostyką modułu.
    /// </summary>
    private static T WithCapturedStderr<T>(string? pin, Func<T> operation)
    {
        string Mask(string text) => pin is null
            ? text
            : text.Replace(Uri.EscapeDataString(pin), "***", StringComparison.Ordinal).Replace(pin, "***", StringComparison.Ordinal);

        if (NativeStderrCapture.Start() is not { } capture)
        {
            throw new HardwareKeyException("nie udało się przechwycić komunikatów modułu PKCS#11 (memfd_create/dup2) — "
                + "przerywam, żeby nie wypisać PIN-u na terminal.");
        }
        using (capture)
        {
            try
            {
                return operation();
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException or InvalidOperationException)
            {
                string native = capture.Stop();
                StringBuilder message = new(Mask(ex.Message));
                foreach (string line in native.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
                {
                    message.Append("\n  ").Append(Mask(line));
                }
                throw new HardwareKeyException(message.ToString());
            }
        }
    }

    /// <summary>
    /// Czy OpenSSL potrafi uruchomić provider. Wołamy ten sam libcrypto, który załadował .NET (ścieżka z /proc/self/maps),
    /// a nie sztywne libcrypto.so.3 — w systemie może być kilka wersji OpenSSL.
    /// </summary>
    private static class OpenSslProvider
    {
        private const string Library = "ksef-libcrypto";

        static OpenSslProvider() => NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, _, _) =>
            string.Equals(name, Library, StringComparison.Ordinal) && LoadedLibcrypto() is { } path ? NativeLibrary.Load(path) : IntPtr.Zero);

        public static bool IsAvailable(string name)
        {
            try
            {
                IntPtr provider = TryLoad(IntPtr.Zero, [.. Encoding.UTF8.GetBytes(name), 0], 1);
                if (provider == IntPtr.Zero)
                {
                    return false;
                }
                _ = Unload(provider);
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return true; // Nie umiemy sprawdzić — zostaje zwykły komunikat błędu z diagnostyką.
            }
        }

        private static string? LoadedLibcrypto()
        {
            _ = SafeEvpPKeyHandle.OpenSslVersion; // .NET ładuje OpenSSL przy pierwszym użyciu.
            try
            {
                return File.ReadLines("/proc/self/maps")
                    .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Where(columns => columns.Length >= 6 && Path.GetFileName(columns[5]).StartsWith("libcrypto.so", StringComparison.Ordinal))
                    .Select(columns => columns[5])
                    .FirstOrDefault();
            }
            catch (IOException)
            {
                return null;
            }
        }

#pragma warning disable SYSLIB1054 // Proste wywołania bez marshallingu wskaźników; LibraryImport wymagałby AllowUnsafeBlocks.
        [DllImport(Library, EntryPoint = "OSSL_PROVIDER_try_load")]
        private static extern IntPtr TryLoad(IntPtr libraryContext, byte[] name, int retainFallbacks);

        [DllImport(Library, EntryPoint = "OSSL_PROVIDER_unload")]
        private static extern int Unload(IntPtr provider);
#pragma warning restore SYSLIB1054
    }

    /// <summary>Natywne stderr (fd 2) do pamięci (memfd) na czas operacji; .NET pisze swoje komunikaty po <see cref="Stop"/>.</summary>
    private sealed class NativeStderrCapture : IDisposable
    {
        private const int StderrFd = 2;
        private readonly int memory;
        private int saved;

        private NativeStderrCapture(int memory, int saved)
        {
            this.memory = memory;
            this.saved = saved;
        }

        /// <summary>Przekierowuje fd 2 do pamięci; null, gdy się nie da (stderr zostaje bez zmian).</summary>
        public static NativeStderrCapture? Start()
        {
            Console.Error.Flush();
            try
            {
                int memoryFd = MemfdCreate("ksef-stderr\0"u8.ToArray(), 0);
                if (memoryFd < 0)
                {
                    return null;
                }
                int savedFd = Dup(StderrFd);
                if (savedFd < 0 || Dup2(memoryFd, StderrFd) < 0)
                {
                    if (savedFd >= 0)
                    {
                        _ = Close(savedFd);
                    }
                    _ = Close(memoryFd);
                    return null;
                }
                return new NativeStderrCapture(memoryFd, savedFd);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }

        /// <summary>Przywraca stderr i zwraca to, co natywny kod na nie wypisał.</summary>
        public string Stop()
        {
            Restore();
            try
            {
                return File.ReadAllText($"/proc/self/fd/{memory}");
            }
            catch (IOException)
            {
                return "";
            }
        }

        public void Dispose()
        {
            Restore();
            _ = Close(memory);
        }

        private void Restore()
        {
            if (saved >= 0)
            {
                _ = Dup2(saved, StderrFd);
                _ = Close(saved);
                saved = -1;
            }
        }

#pragma warning disable SYSLIB1054 // Proste wywołania libc bez marshallingu wskaźników; LibraryImport wymagałby AllowUnsafeBlocks.
        [DllImport("libc", EntryPoint = "memfd_create", SetLastError = true)]
        private static extern int MemfdCreate(byte[] name, uint flags);

        [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
        private static extern int Dup(int fd);

        [DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
        private static extern int Dup2(int oldFd, int newFd);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int Close(int fd);
#pragma warning restore SYSLIB1054
    }
}
