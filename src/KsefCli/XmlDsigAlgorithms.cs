using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Security.Cryptography.Xml;

namespace KsefCli;

/// <summary>
/// SignedXml (podpis XAdES przy logowaniu) tworzy skrót referencji i formatter podpisu RSA po nazwie
/// przez CryptoConfig, który bez rejestracji sięga po implementację refleksją — w Native AOT wyciętą.
/// Rejestracja typem działa w obu trybach. ECDSA rejestruje sama biblioteka MF.
/// </summary>
public static class XmlDsigAlgorithms
{
    private static readonly Lazy<bool> Registered = new(() =>
    {
        CryptoConfig.AddAlgorithm(typeof(Sha256), SignedXml.XmlDsigSHA256Url, "SHA256");
        CryptoConfig.AddAlgorithm(typeof(RSAPKCS1SignatureFormatter), typeof(RSAPKCS1SignatureFormatter).AssemblyQualifiedName!);
        CryptoConfig.AddAlgorithm(typeof(RSAPKCS1SignatureDeformatter), typeof(RSAPKCS1SignatureDeformatter).AssemblyQualifiedName!);
        return true;
    });

    // CryptoConfig wywołuje konstruktory przez refleksję — w AOT muszą zostać zachowane.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(Sha256))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(RSAPKCS1SignatureFormatter))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(RSAPKCS1SignatureDeformatter))]
    public static void Register() => _ = Registered.Value;

    /// <summary>SHA-256 z publicznym konstruktorem bezparametrowym, czego wymaga CryptoConfig (typ musi być publiczny).</summary>
    public sealed class Sha256 : HashAlgorithm
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public Sha256() => HashSizeValue = 256;

        public override void Initialize() => hash.GetHashAndReset();

        protected override void HashCore(byte[] array, int ibStart, int cbSize) => hash.AppendData(array, ibStart, cbSize);

        protected override void HashCore(ReadOnlySpan<byte> source) => hash.AppendData(source);

        protected override byte[] HashFinal() => hash.GetHashAndReset();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                hash.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
