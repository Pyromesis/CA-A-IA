// CA-A-IA — Secretos en el Credential Manager de Windows (advapi32, P/Invoke directo).
// Bóveda estándar del usuario: visible en "Credential Manager", no depende de ficheros propios.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using CaAIA.Domain.Security;
using Microsoft.Extensions.Logging;

namespace CaAIA.Infrastructure.Security;

/// <summary>
/// <see cref="ISecretStore"/> sobre credenciales genéricas (<c>CA-A-IA/&lt;key&gt;</c>,
/// persistencia local-machine). Sin paquetes: P/Invoke a <c>advapi32.dll</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialManagerSecretStore : ISecretStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const string Prefix = "CA-A-IA/";
    private const int NOT_FOUND = 1168;

    private readonly ILogger<CredentialManagerSecretStore> _log;

    public CredentialManagerSecretStore(ILogger<CredentialManagerSecretStore> log)
    {
        _log = log;
    }

    public Task StoreAsync(string key, string secret, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(secret);

        var blob = Encoding.Unicode.GetBytes(secret);
        var handle = Marshal.AllocCoTaskMem(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, handle, blob.Length);
            var credential = new NativeCredential
            {
                Flags = 0,
                Type = CRED_TYPE_GENERIC,
                TargetName = Prefix + key,
                Comment = "CA-A-IA secret",
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = handle,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CredWrite failed for '{key}'.");
            }
        }
        finally
        {
            Array.Clear(blob);
            Marshal.FreeCoTaskMem(handle);
        }

        return Task.CompletedTask;
    }

    public Task<string?> RetrieveAsync(string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        var ptr = IntPtr.Zero;
        try
        {
            if (!CredRead(Prefix + key, CRED_TYPE_GENERIC, 0, out ptr))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == NOT_FOUND)
                {
                    return Task.FromResult<string?>(null);
                }

                throw new Win32Exception(error, $"CredRead failed for '{key}'.");
            }

            var credential = Marshal.PtrToStructure<NativeCredential>(ptr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return Task.FromResult<string?>(null);
            }

            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            return Task.FromResult<string?>(Encoding.Unicode.GetString(blob));
        }
        finally
        {
            if (ptr != IntPtr.Zero)
            {
                CredFree(ptr);
            }
        }
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        if (!CredDelete(Prefix + key, CRED_TYPE_GENERIC, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NOT_FOUND)
            {
                throw new Win32Exception(error, $"CredDelete failed for '{key}'.");
            }
        }

        return Task.CompletedTask;
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Secret key is required.", nameof(key));
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string Comment;
        public long LastWritten; // FILETIME
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
