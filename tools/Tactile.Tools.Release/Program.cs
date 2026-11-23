using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tactile.Core.Catalog;

// Release helper for the update channel (docs/contract-phase7b.md, "Manifest").
//   keygen                      create the signing key once (Windows key store, current user)
//   pubkey                      print the public key (SubjectPublicKeyInfo, base64)
//   backup <file> <password>    write a password-encrypted PKCS#8 copy of the key
//   restore <file> <password>   import such a copy into the key store (new PC)
//   manifest <version> <dir> <notes-en> <notes-tr>
//                               write <dir>/manifest.json and <dir>/manifest.json.sig
//                               for TriggerForge-<version>-setup.exe and -portable.zip in <dir>
//   verify <dir> <pubkey>       check <dir>/manifest.json.sig against a public key (base64)
//   catalog keygen | pubkey | backup <file> <password> | restore <file> <password>
//                               the same for the community catalog key
//   catalog sign <repo-dir>     refresh sizes, hashes and summaries, then sign <repo-dir>/index.json
//   catalog verify <repo-dir> <pubkey>
//                               check index.json, its signature and every profile file
internal static class Program
{
    private const string Repo = "https://github.com/kaan237/triggerforge/releases/download/";

    private const string KeyName = "TriggerForge update signing";
    private const string CatalogKeyName = "TriggerForge catalog signing";

    private static int Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["keygen"] => KeyGen(),
                ["pubkey"] => PubKey(),
                ["backup", var file, var password] => Backup(file, password),
                ["restore", var file, var password] => Restore(file, password),
                ["manifest", var version, var dir, var en, var tr] => Manifest(version, dir, en, tr),
                ["verify", var dir, var publicKey] => Verify(dir, publicKey),
                ["catalog", "keygen"] => KeyGen(CatalogKeyName),
                ["catalog", "pubkey"] => PubKey(CatalogKeyName),
                ["catalog", "backup", var file, var password] => Backup(file, password, CatalogKeyName),
                ["catalog", "restore", var file, var password] => Restore(file, password, CatalogKeyName),
                ["catalog", "sign", var repoDir] => CatalogSign(repoDir),
                ["catalog", "verify", var repoDir, var publicKey] => CatalogVerify(repoDir, publicKey),
                _ => Usage(),
            };
        }
        catch (Exception e) when (e is IOException or CryptographicException or ArgumentException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: keygen | pubkey | backup <file> <password> | restore <file> <password> | manifest <version> <dir> <notes-en> <notes-tr> | verify <dir> <pubkey> | catalog keygen | catalog pubkey | catalog backup <file> <password> | catalog restore <file> <password> | catalog sign <repo-dir> | catalog verify <repo-dir> <pubkey>");
        return 2;
    }

    private static int KeyGen(string keyName = KeyName)
    {
        if (CngKey.Exists(keyName))
        {
            Console.Error.WriteLine($"The key \"{keyName}\" already exists; it is never replaced.");
            return 1;
        }

        var parameters = new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.AllowExport };
        using CngKey cng = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, parameters);
        using var key = new ECDsaCng(cng);
        Console.WriteLine($"Key \"{keyName}\" created in the current user's key store. Run 'backup' and keep the file safe: without the key nothing can be signed.");
        Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }

    private static ECDsa LoadKey(string keyName = KeyName) => new ECDsaCng(CngKey.Open(keyName));

    private static int Backup(string file, string password, string keyName = KeyName)
    {
        using ECDsa key = LoadKey(keyName);
        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);
        File.WriteAllBytes(file, key.ExportEncryptedPkcs8PrivateKey(password, pbe));
        Console.WriteLine($"Encrypted key written to {file}.");
        return 0;
    }

    private static int Restore(string file, string password, string keyName = KeyName)
    {
        if (CngKey.Exists(keyName))
        {
            Console.Error.WriteLine($"The key \"{keyName}\" already exists; it is never replaced.");
            return 1;
        }

        using var imported = ECDsa.Create();
        imported.ImportEncryptedPkcs8PrivateKey(password, File.ReadAllBytes(file), out _);
        ECParameters parameters = imported.ExportParameters(includePrivateParameters: true);
        var creation = new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.AllowExport };
        using CngKey cng = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, creation);
        using var key = new ECDsaCng(cng);
        key.ImportParameters(parameters);
        CryptographicOperations.ZeroMemory(parameters.D);
        Console.WriteLine($"Key \"{keyName}\" restored.");
        Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }

    private static int PubKey(string keyName = KeyName)
    {
        using ECDsa key = LoadKey(keyName);
        Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }

    private static int Manifest(string version, string dir, string notesEn, string notesTr)
    {
        if (!Version.TryParse(version, out _))
        {
            throw new ArgumentException($"Not a version: {version}");
        }

        JsonObject Asset(string name)
        {
            string path = Path.Combine(dir, name);
            using FileStream file = File.OpenRead(path);
            return new JsonObject
            {
                ["name"] = name,
                ["url"] = $"{Repo}v{version}/{name}",
                ["size"] = file.Length,
                ["sha256"] = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(),
            };
        }

        var manifest = new JsonObject
        {
            ["schema"] = 1,
            ["version"] = version,
            ["released"] = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["notes"] = new JsonObject { ["en"] = notesEn, ["tr"] = notesTr },
            ["installer"] = Asset($"TriggerForge-{version}-setup.exe"),
            ["portable"] = Asset($"TriggerForge-{version}-portable.zip"),
        };

        byte[] body = Encoding.UTF8.GetBytes(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        using ECDsa key = LoadKey();
        byte[] signature = key.SignData(body, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        File.WriteAllBytes(Path.Combine(dir, "manifest.json"), body);
        File.WriteAllText(Path.Combine(dir, "manifest.json.sig"), Convert.ToBase64String(signature));
        Console.WriteLine($"manifest.json and manifest.json.sig written to {dir}");
        return 0;
    }

    private static int Verify(string dir, string publicKey)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        byte[] body = File.ReadAllBytes(Path.Combine(dir, "manifest.json"));
        byte[] signature = Convert.FromBase64String(File.ReadAllText(Path.Combine(dir, "manifest.json.sig")).Trim());
        bool ok = key.VerifyData(body, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Console.WriteLine(ok ? "Manifest signature is valid." : "Manifest signature is NOT valid.");
        return ok ? 0 : 1;
    }

    private static int CatalogSign(string repoDir)
    {
        using ECDsa key = LoadKey(CatalogKeyName);
        CatalogSigner.Sign(repoDir, key, DateTime.UtcNow);
        Console.WriteLine($"index.json and index.json.sig written to {repoDir}");
        return 0;
    }

    private static int CatalogVerify(string repoDir, string publicKey)
    {
        int count = CatalogSigner.Verify(repoDir, Convert.FromBase64String(publicKey));
        Console.WriteLine($"Catalog signature and all profile entries are valid ({count} profiles).");
        return 0;
    }
}
