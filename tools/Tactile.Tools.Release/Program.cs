using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Release helper for the update channel (docs/contract-phase7b.md, "Manifest").
//   keygen                      create the signing key once (Windows key store, current user)
//   pubkey                      print the public key (SubjectPublicKeyInfo, base64)
//   backup <file> <password>    write a password-encrypted PKCS#8 copy of the key
//   manifest <version> <dir> <notes-en> <notes-tr>
//                               write <dir>/manifest.json and <dir>/manifest.json.sig
//                               for TriggerForge-<version>-setup.exe and -portable.zip in <dir>
//   verify <dir> <pubkey>       check <dir>/manifest.json.sig against a public key (base64)
internal static class Program
{
    private const string Repo = "https://github.com/kaan237/triggerforge/releases/download/";

    private const string KeyName = "TriggerForge update signing";

    private static int Main(string[] args)
    {
        try
        {
            return args switch
            {
                ["keygen"] => KeyGen(),
                ["pubkey"] => PubKey(),
                ["backup", var file, var password] => Backup(file, password),
                ["manifest", var version, var dir, var en, var tr] => Manifest(version, dir, en, tr),
                ["verify", var dir, var publicKey] => Verify(dir, publicKey),
                _ => Usage(),
            };
        }
        catch (Exception e) when (e is IOException or CryptographicException or ArgumentException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: keygen | pubkey | backup <file> <password> | manifest <version> <dir> <notes-en> <notes-tr> | verify <dir> <pubkey>");
        return 2;
    }

    private static int KeyGen()
    {
        if (CngKey.Exists(KeyName))
        {
            Console.Error.WriteLine($"The key \"{KeyName}\" already exists; it is never replaced.");
            return 1;
        }

        var parameters = new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.AllowExport };
        using CngKey cng = CngKey.Create(CngAlgorithm.ECDsaP256, KeyName, parameters);
        using var key = new ECDsaCng(cng);
        Console.WriteLine($"Key \"{KeyName}\" created in the current user's key store. Run 'backup' and keep the file safe: without the key no update can be signed.");
        Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        return 0;
    }

    private static ECDsa LoadKey() => new ECDsaCng(CngKey.Open(KeyName));

    private static int Backup(string file, string password)
    {
        using ECDsa key = LoadKey();
        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);
        File.WriteAllBytes(file, key.ExportEncryptedPkcs8PrivateKey(password, pbe));
        Console.WriteLine($"Encrypted key written to {file}.");
        return 0;
    }

    private static int PubKey()
    {
        using ECDsa key = LoadKey();
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
}
