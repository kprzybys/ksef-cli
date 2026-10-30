using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace KsefCli.Commands;

/// <summary>
/// Klucz prywatny, który nie opuszcza karty, tokenu ani TPM (`ksef login --cert cert.pem --key pkcs11:…`).
/// Podpis XAdES robi OpenSSL systemu przez moduł PKCS#11: engine `pkcs11` (libp11) albo provider `pkcs11`
/// (pkcs11-provider). Domyślny moduł obu to p11-kit, który widzi wszystkie zarejestrowane karty (OpenSC, TPM, SoftHSM).
/// </summary>
public static partial class LoginCommand
{
    // pkcs11-provider na OpenSSL 3.0 zakleszcza się z modułami, które same używają OpenSSL (np. SoftHSM),
    // dlatego przed 3.2 domyślnie engine. Wartość z SafeEvpPKeyHandle.OpenSslVersion: 0xMNN00PP0.
    private const long FirstProviderDefaultVersion = 0x3020_0000;

    private sealed record HardwareKey(string Uri, string? PinFile, string? Via);

    private static bool TryAttachHardwareKey(X509Certificate2 certificate, HardwareKey key, [NotNullWhen(true)] out X509Certificate2? withKey)
    {
        withKey = null;
        string? pin = null;
        if (key.PinFile is { } pinFile)
        {
            try
            {
                pin = Encoding.UTF8.GetString(CliIo.ReadAllBytes(pinFile))
                    .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
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
        // PIN tylko w pamięci, jako atrybut URI (RFC 7512) — nigdy w argumentach procesu ani w komunikatach.
        string uri = pin is null
            ? key.Uri
            : $"{key.Uri}{(key.Uri.Contains('?', StringComparison.Ordinal) ? '&' : '?')}pin-value={Uri.EscapeDataString(pin)}";
        // libp11 i moduły PKCS#11 piszą wprost na fd 2 — przy błędzie także URI z PIN-em, a engine na OpenSSL 3.0
        // przy każdym użyciu wiersz „Workaround…”. Przechwytujemy to: przy sukcesie cisza, przy błędzie bez PIN-u.
        using NativeStderrCapture capture = new();
        try
        {
            using SafeEvpPKeyHandle handle = viaProvider
                ? SafeEvpPKeyHandle.OpenKeyFromProvider("pkcs11", uri)
                : SafeEvpPKeyHandle.OpenPrivateKeyFromEngine("pkcs11", uri);
            if (certificate.GetRSAPublicKey() is { } rsaPublic)
            {
                rsaPublic.Dispose();
                using RSAOpenSsl rsa = new(handle);
                withKey = certificate.CopyWithPrivateKey(rsa);
            }
            else
            {
                using ECDsaOpenSsl ecdsa = new(handle);
                withKey = certificate.CopyWithPrivateKey(ecdsa);
            }
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            string native = capture.Stop();
            if (MissingModule(viaProvider, ex) is { } missing)
            {
                CliIo.Error($"{key.Uri}: {missing}");
                return false;
            }
            string Mask(string text) => pin is null ? text : text.Replace(Uri.EscapeDataString(pin), "***", StringComparison.Ordinal).Replace(pin, "***", StringComparison.Ordinal);
            CliIo.Error($"{key.Uri}: nie udało się użyć klucza przez OpenSSL {(viaProvider ? "provider" : "engine")} pkcs11 "
                + $"(zły PIN, brak karty albo klucz nie pasuje do certyfikatu?): {Mask(ex.Message)}");
            foreach (string line in native.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
            {
                Console.Error.WriteLine($"  {Mask(line)}");
            }
            return false;
        }
    }

    /// <summary>
    /// Brak samego modułu OpenSSL to najczęstszy błąd na świeżym systemie, a OpenSSL zgłasza go surowo
    /// („no such engine”, „reason(524325)”) — mówimy wprost, czego brakuje i jak to obejść.
    /// </summary>
    private static string? MissingModule(bool viaProvider, Exception ex)
    {
        if (!viaProvider && ex.Message.Contains("no such engine", StringComparison.Ordinal))
        {
            return "OpenSSL nie ma engine'u pkcs11 — zainstaluj libp11 (Debian/Ubuntu: libengine-pkcs11-openssl, "
                + "Fedora/RHEL 9: openssl-pkcs11) albo użyj --key-via provider (pakiet pkcs11-provider).";
        }
        if (viaProvider && !OpenSslProvider.IsAvailable("pkcs11"))
        {
            return "OpenSSL nie ma providera pkcs11 — zainstaluj pkcs11-provider albo użyj --key-via engine "
                + "(Debian/Ubuntu: libengine-pkcs11-openssl).";
        }
        return null;
    }

    /// <summary>Czy OpenSSL potrafi załadować provider (ten sam libcrypto, którego używa .NET).</summary>
    private static class OpenSslProvider
    {
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
                return true; // Nie umiemy sprawdzić — zostaje zwykły komunikat błędu.
            }
        }

#pragma warning disable SYSLIB1054 // Jak w NativeStderrCapture: bez marshallingu wskaźników.
        [DllImport("libcrypto.so.3", EntryPoint = "OSSL_PROVIDER_try_load")]
        private static extern IntPtr TryLoad(IntPtr libraryContext, byte[] name, int retainFallbacks);

        [DllImport("libcrypto.so.3", EntryPoint = "OSSL_PROVIDER_unload")]
        private static extern int Unload(IntPtr provider);
#pragma warning restore SYSLIB1054
    }

    /// <summary>Natywne stderr (fd 2) do pamięci (memfd) na czas operacji; .NET pisze swoje komunikaty po <see cref="Stop"/>.</summary>
    private sealed class NativeStderrCapture : IDisposable
    {
        private const int StderrFd = 2;
        private readonly int memory = -1;
        private int saved = -1;

        public NativeStderrCapture()
        {
            Console.Error.Flush();
            memory = MemfdCreate("ksef-stderr\0"u8.ToArray(), 0);
            if (memory < 0)
            {
                return;
            }
            saved = Dup(StderrFd);
            if (saved < 0 || Dup2(memory, StderrFd) < 0)
            {
                _ = Close(memory);
                memory = -1;
            }
        }

        /// <summary>Przywraca stderr i zwraca to, co natywny kod na nie wypisał.</summary>
        public string Stop()
        {
            if (memory < 0)
            {
                return "";
            }
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
            if (memory >= 0)
            {
                Restore();
                _ = Close(memory);
            }
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
