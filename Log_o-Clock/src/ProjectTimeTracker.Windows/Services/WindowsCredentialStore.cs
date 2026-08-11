using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProjectTimeTracker.Core;

namespace ProjectTimeTracker.Windows.Services;

public sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;

    public Task<TrelloCredentials?> GetTrelloCredentialsAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = GetTarget(profileId);
        if (!CredRead(target, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            return error == 1168
                ? Task.FromResult<TrelloCredentials?>(null)
                : throw new Win32Exception(error, "Windows could not read the Trello credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return Task.FromResult<TrelloCredentials?>(null);
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var value = JsonSerializer.Deserialize<TrelloCredentials>(Encoding.UTF8.GetString(bytes))
                ?? throw new InvalidDataException("The saved Trello credential is invalid.");
            return Task.FromResult<TrelloCredentials?>(value);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The saved Trello credential is invalid.", exception);
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public Task SetTrelloCredentialsAsync(
        Guid profileId,
        TrelloCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.ApiKey) || string.IsNullOrWhiteSpace(credentials.Token))
        {
            throw new ArgumentException("Both the Trello API key and token are required.", nameof(credentials));
        }

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(credentials));
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var native = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = GetTarget(profileId),
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = "Log O'clock",
            };
            if (!CredWrite(ref native, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not save the Trello credential.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.FreeCoTaskMem(blob);
        }

        return Task.CompletedTask;
    }

    public Task DeleteTrelloCredentialsAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CredDelete(GetTarget(profileId), CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168)
            {
                throw new Win32Exception(error, "Windows could not delete the Trello credential.");
            }
        }

        return Task.CompletedTask;
    }

    public Task<GoogleSheetsCredentials?> GetGoogleSheetsCredentialsAsync(
        Guid profileId,
        CancellationToken cancellationToken = default) =>
        ReadAsync<GoogleSheetsCredentials>(
            GetGoogleTarget(profileId),
            "Google Sheets",
            cancellationToken);

    public Task SetGoogleSheetsCredentialsAsync(
        Guid profileId,
        GoogleSheetsCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.ClientId) ||
            string.IsNullOrWhiteSpace(credentials.ClientSecret) ||
            string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            throw new ArgumentException(
                "The Google desktop client ID, client secret, and refresh token are required.",
                nameof(credentials));
        }

        return WriteAsync(GetGoogleTarget(profileId), credentials, "Google Sheets", cancellationToken);
    }

    public Task DeleteGoogleSheetsCredentialsAsync(
        Guid profileId,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(GetGoogleTarget(profileId), "Google Sheets", cancellationToken);

    private static Task<T?> ReadAsync<T>(
        string target,
        string provider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CredRead(target, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            return error == 1168
                ? Task.FromResult<T?>(default)
                : throw new Win32Exception(error, $"Windows could not read the {provider} credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return Task.FromResult<T?>(default);
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var value = JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(bytes));
            return Task.FromResult(value);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The saved {provider} credential is invalid.", exception);
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    private static Task WriteAsync<T>(
        string target,
        T value,
        string provider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var native = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = "Log O'clock",
            };
            if (!CredWrite(ref native, 0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Windows could not save the {provider} credential.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.FreeCoTaskMem(blob);
        }

        return Task.CompletedTask;
    }

    private static Task DeleteAsync(
        string target,
        string provider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CredDelete(target, CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168)
            {
                throw new Win32Exception(error, $"Windows could not delete the {provider} credential.");
            }
        }

        return Task.CompletedTask;
    }

    private static string GetTarget(Guid profileId) => $"ProjectTimeTracker/Trello/{profileId:N}";
    private static string GetGoogleTarget(Guid profileId) => $"ProjectTimeTracker/GoogleSheets/{profileId:N}";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
