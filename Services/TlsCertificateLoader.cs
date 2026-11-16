using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace QuasarBridge.Services;

public static class TlsCertificateLoader
{
    /// <summary>
    /// Loads end-entity + intermediates for Kestrel.
    /// Prefer an already-imported Windows store cert (stable server key on Schannel).
    /// Fall back to PEM/PFX import with UserKeySet.
    /// </summary>
    public static (X509Certificate2 Leaf, X509Certificate2Collection Intermediates) Load(string certPath, string? keyPath, string? password, string? storeThumbprint = null)
    {
        if (string.IsNullOrWhiteSpace(certPath))
            throw new InvalidOperationException("TlsCertPath is empty");

        var fullCertPath = Path.GetFullPath(certPath);
        if (!File.Exists(fullCertPath))
            throw new FileNotFoundException("TLS certificate not found", fullCertPath);

        if (!string.IsNullOrWhiteSpace(storeThumbprint))
        {
            var fromStore = TryLoadFromStore(storeThumbprint);
            if (fromStore is not null)
            {
                var intermediates = LoadIntermediatesFromPemOrPfx(fullCertPath, keyPath, password, fromStore.Value.Leaf.Thumbprint!);
                return (fromStore.Value.Leaf, intermediates);
            }
        }

        // Auto-detect by subject if store already has quasar leaf.
        var auto = TryFindQuasarInStore();
        if (auto is not null)
        {
            var intermediates = LoadIntermediatesFromPemOrPfx(fullCertPath, keyPath, password, auto.Value.Leaf.Thumbprint!);
            return (auto.Value.Leaf, intermediates);
        }

        if (fullCertPath.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
            fullCertPath.EndsWith(".p12", StringComparison.OrdinalIgnoreCase))
            return LoadPfx(fullCertPath, password);

        return LoadPem(fullCertPath, keyPath);
    }

    private static (X509Certificate2 Leaf, X509Certificate2Collection Intermediates)? TryLoadFromStore(string thumbprint)
    {
        var normalized = thumbprint.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates
            .Cast<X509Certificate2>()
            .FirstOrDefault(c => c.Thumbprint?.Equals(normalized, StringComparison.OrdinalIgnoreCase) == true && c.HasPrivateKey);
        if (found is null) return null;
        return (found, new X509Certificate2Collection());
    }

    private static (X509Certificate2 Leaf, X509Certificate2Collection Intermediates)? TryFindQuasarInStore()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates
            .Cast<X509Certificate2>()
            .Where(c => c.HasPrivateKey && c.Subject.Contains("quasar.yandex.net", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.NotAfter)
            .FirstOrDefault();
        if (found is null) return null;
        return (found, new X509Certificate2Collection());
    }

    private static X509Certificate2Collection LoadIntermediatesFromPemOrPfx(string certPath, string? keyPath, string? password, string leafThumbprint)
    {
        var intermediates = new X509Certificate2Collection();
        if (certPath.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) || certPath.EndsWith(".p12", StringComparison.OrdinalIgnoreCase))
        {
            var collection = X509CertificateLoader.LoadPkcs12CollectionFromFile(
                certPath, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
            foreach (var c in collection.Cast<X509Certificate2>().Where(c => !c.HasPrivateKey))
                intermediates.Add(c);
            return intermediates;
        }

        var certPem = File.ReadAllText(certPath);
        foreach (var part in certPem.Split("-----END CERTIFICATE-----", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!part.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal)) continue;
            var block = part + "\n-----END CERTIFICATE-----\n";
            using var cert = X509Certificate2.CreateFromPem(block);
            if (string.Equals(cert.Thumbprint, leafThumbprint, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(cert.Subject, cert.Issuer, StringComparison.Ordinal)) continue;
            intermediates.Add(X509CertificateLoader.LoadCertificate(cert.RawData));
        }
        return intermediates;
    }

    private static (X509Certificate2 Leaf, X509Certificate2Collection Intermediates) LoadPfx(string fullCertPath, string? password)
    {
        var leafBytes = File.ReadAllBytes(fullCertPath);
        var leaf = LoadPkcs12WithFallback(leafBytes, password);
        var intermediates = LoadIntermediatesFromPemOrPfx(fullCertPath, null, password, leaf.Thumbprint!);
        return (leaf, intermediates);
    }

    private static (X509Certificate2 Leaf, X509Certificate2Collection Intermediates) LoadPem(string fullCertPath, string? keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath))
            throw new InvalidOperationException("TlsKeyPath is required for PEM certificates");

        var fullKeyPath = Path.GetFullPath(keyPath);
        if (!File.Exists(fullKeyPath))
            throw new FileNotFoundException("TLS private key not found", fullKeyPath);

        var certPem = File.ReadAllText(fullCertPath);
        var keyPem = File.ReadAllText(fullKeyPath);
        using var pemLeaf = X509Certificate2.CreateFromPem(certPem, keyPem);
        var leafBytes = pemLeaf.Export(X509ContentType.Pkcs12, "quasar-temp");
        var leaf = LoadPkcs12WithFallback(leafBytes, "quasar-temp");
        var intermediates = LoadIntermediatesFromPemOrPfx(fullCertPath, keyPath, null, pemLeaf.Thumbprint!);
        return (leaf, intermediates);
    }

    private static X509Certificate2 LoadPkcs12WithFallback(byte[] pfxBytes, string? password)
    {
        CryptographicException? last = null;
        foreach (var flags in new[]
                 {
                     X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet,
                     X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet,
                     X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet
                 })
        {
            try { return X509CertificateLoader.LoadPkcs12(pfxBytes, password, flags); }
            catch (CryptographicException ex) { last = ex; }
        }

        throw new CryptographicException(
            "Cannot import TLS private key into Windows key store. Import certs\\quasar-mitm.pfx into CurrentUser\\My once (certutil -user -importpfx), then set TlsStoreThumbprint.",
            last);
    }
}
