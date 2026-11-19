using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tactile.Core.Updates;

/// <summary>
/// Checks the Authenticode signature of an open file with WinVerifyTrust: the file digest must
/// match its signature. The only accepted chain problem is an untrusted root, because the
/// installer is signed with a self-signed certificate; the signer itself is pinned separately.
/// </summary>
internal static unsafe partial class Authenticode
{
    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint StateVerify = 1;
    private const uint StateClose = 2;
    // No network: revocation and URL retrieval are off, so a check never waits on a server.
    private const uint RevocationCheckNone = 0x10;
    private const uint CacheOnlyUrlRetrieval = 0x1000;
    private const int UntrustedRoot = unchecked((int)0x800B0109);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static bool IsIntact(SafeFileHandle file, string path)
    {
        fixed (char* name = path)
        {
            var info = new FileInfo
            {
                Size = (uint)sizeof(FileInfo),
                Path = name,
                Handle = file.DangerousGetHandle(),
            };
            var data = new TrustData
            {
                Size = (uint)sizeof(TrustData),
                UiChoice = UiNone,
                RevocationChecks = RevokeNone,
                UnionChoice = ChoiceFile,
                File = &info,
                StateAction = StateVerify,
                ProviderFlags = RevocationCheckNone | CacheOnlyUrlRetrieval,
            };

            Guid action = GenericVerifyV2;
            bool added = false;
            file.DangerousAddRef(ref added);
            try
            {
                int result = WinVerifyTrust(-1, &action, &data);
                data.StateAction = StateClose;
                _ = WinVerifyTrust(-1, &action, &data);
                return result is 0 or UntrustedRoot;
            }
            finally
            {
                if (added)
                {
                    file.DangerousRelease();
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo
    {
        public uint Size;
        public char* Path;
        public nint Handle;
        public Guid* KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public FileInfo* File;
        public uint StateAction;
        public nint StateData;
        public char* UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint window, Guid* action, TrustData* data);
}
