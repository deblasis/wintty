using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Sponsor.Auth;

/// <summary>
/// DPAPI-backed <see cref="IJwtStore"/>. Persists under
/// <c>{root}/auth.bin</c> using <see cref="ProtectedData"/> with
/// <see cref="DataProtectionScope.CurrentUser"/> and an app-specific
/// entropy blob so the envelope is bound to the current user and this
/// app's identity. Atomic writes via <c>.partial</c> + <c>File.Move</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DpapiJwtStore : IJwtStore
{
    /// <summary>
    /// Label embedded in the DPAPI entropy blob. Not a secret; bounds
    /// the envelope to this app so other user-scoped applications
    /// calling ProtectedData.Unprotect with default entropy can't read
    /// our JWT. Increment the -vN suffix if the JWT format ever
    /// changes in a backwards-incompatible way.
    /// </summary>
    public const string EntropyLabelV1 = "wintty-sponsor-jwt-v1";

    /// <summary>
    /// Default entropy bytes for DPAPI encryption. Exposed as
    /// <see cref="ReadOnlySpan{Byte}"/> backed by a UTF-8 string literal
    /// so the data lives in the PE's read-only section - no heap array
    /// that an errant caller can mutate out from under every
    /// subsequent encrypt/decrypt.
    /// </summary>
    public static ReadOnlySpan<byte> DefaultEntropy => "wintty-sponsor-jwt-v1"u8;

    private const string FileName = "auth.bin";
    private const string PartialSuffix = ".partial";

    private readonly string _root;
    private readonly byte[] _entropy;

    public DpapiJwtStore(string root, ReadOnlySpan<byte> entropy)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        if (entropy.IsEmpty)
            throw new ArgumentException("entropy must not be empty", nameof(entropy));
        _root = root;
        // Defensive copy: the caller could be handing us a pooled
        // buffer or a slice of a larger array. We need a stable blob
        // for the lifetime of this store.
        _entropy = entropy.ToArray();

        Directory.CreateDirectory(_root);
    }

    private string Target  => Path.Combine(_root, FileName);
    private string Partial => Path.Combine(_root, FileName + PartialSuffix);

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT",
        "IL3050:RequiresDynamicCode",
        Justification = "ProtectedData.Protect/Unprotect are trim-safe P/Invoke wrappers; no dynamic codegen.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "ProtectedData.Protect/Unprotect do not require reflection on application types.")]
    public async Task<byte[]?> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(Target))
            return null;

        try
        {
            var encrypted = await File.ReadAllBytesAsync(Target, ct).ConfigureAwait(false);
            // Note: ProtectedData.Unprotect has no async equivalent. It is a
            // fast in-memory call (microseconds for our ~1KB JWT) so the
            // sync call on the awaiting thread is fine.
            var plain = ProtectedData.Unprotect(encrypted, _entropy, DataProtectionScope.CurrentUser);
            return plain;
        }
        catch (CryptographicException)
        {
            // Entropy mismatch, corrupted blob, or a different user's
            // session. Treat as "no valid token" and let the caller
            // decide whether to delete.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT",
        "IL3050:RequiresDynamicCode",
        Justification = "ProtectedData.Protect/Unprotect are trim-safe P/Invoke wrappers; no dynamic codegen.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "ProtectedData.Protect/Unprotect do not require reflection on application types.")]
    public async Task WriteAsync(byte[] utf8Token, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(utf8Token);

        var encrypted = ProtectedData.Protect(utf8Token, _entropy, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(Partial, encrypted, ct).ConfigureAwait(false);
        // File.Move with overwrite:true is atomic on NTFS when source and
        // target are on the same volume. LocalApplicationData lives on the
        // user profile drive, so Partial and Target are always co-located.
        File.Move(Partial, Target, overwrite: true);
    }

    public Task DeleteAsync(CancellationToken ct)
    {
        // Surface IOException / UnauthorizedAccessException to the
        // caller (OAuthTokenProvider.SafeDeleteAsync) so the single
        // logger call there sees the actual failure instead of a
        // silent success. Double-swallow was losing the only useful
        // signal we had for "JWT still on disk after sign-out".
        if (File.Exists(Target))
            File.Delete(Target);
        if (File.Exists(Partial))
            File.Delete(Partial);
        return Task.CompletedTask;
    }
}
