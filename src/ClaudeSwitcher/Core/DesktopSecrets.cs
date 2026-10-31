using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSwitcher.Core;

/// <summary>One OAuth credential cached by Claude Desktop for its Code tab.</summary>
public sealed record DesktopCredential(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string AccountUuid,
    string OrgUuid,
    string ApiHost,
    string? SubscriptionType,
    string? RateLimitTier)
{
    public bool IsExpired => ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1);
}

/// <summary>
/// Reads values Claude Desktop stores with Electron safeStorage: "v10" + AES-256-GCM, whose key
/// lives DPAPI-protected in the profile's "Local State" file.
/// </summary>
public static class DesktopSecrets
{
    const string TokenCacheV2 = "oauth:tokenCacheV2";
    const string TokenCacheV1 = "oauth:tokenCache";

    public static byte[] ReadMasterKey(string dataDir)
    {
        var localState = JsonNode.Parse(ReadShared(Path.Combine(dataDir, "Local State")))!;
        var wrapped = Convert.FromBase64String((string)localState["os_crypt"]!["encrypted_key"]!);
        if (Encoding.ASCII.GetString(wrapped, 0, 5) != "DPAPI")
            throw new InvalidDataException("Unexpected os_crypt key format.");
        return DpapiUnprotect(wrapped.AsSpan(5).ToArray());
    }

    public static string Decrypt(string base64, byte[] key)
    {
        var blob = Convert.FromBase64String(base64);
        if (blob.Length < 3 + 12 + 16 || Encoding.ASCII.GetString(blob, 0, 3) != "v10")
            throw new InvalidDataException("Unsupported safeStorage payload.");
        var nonce = blob.AsSpan(3, 12);
        var tag = blob.AsSpan(blob.Length - 16);
        var cipher = blob.AsSpan(15, blob.Length - 15 - 16);
        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>The most likely working credential, or null when signed out.</summary>
    public static DesktopCredential? ReadCredential(string dataDir) => ReadCredentials(dataDir).FirstOrDefault();

    /// <summary>
    /// All cached credentials, best first: unexpired, then the current v2 cache (the legacy v1 cache can hold
    /// tokens the server has already revoked), then inference scope, then latest expiry.
    /// </summary>
    public static IReadOnlyList<DesktopCredential> ReadCredentials(string dataDir)
    {
        var configPath = Path.Combine(dataDir, "config.json");
        if (!File.Exists(configPath) || !File.Exists(Path.Combine(dataDir, "Local State"))) return Array.Empty<DesktopCredential>();
        var config = JsonNode.Parse(ReadShared(configPath)) as JsonObject;
        if (config == null) return Array.Empty<DesktopCredential>();
        var key = ReadMasterKey(dataDir);

        var candidates = new List<(DesktopCredential Cred, bool Inference, int CacheRank)>();
        var caches = new[] { TokenCacheV2, TokenCacheV1 };
        for (var rank = 0; rank < caches.Length; rank++)
        {
            var cacheName = caches[rank];
            if (config[cacheName] is not JsonValue raw || raw.GetValue<string>() is not { Length: > 0 } enc) continue;
            JsonObject? cache;
            try { cache = JsonNode.Parse(Decrypt(enc, key)) as JsonObject; }
            catch (CryptographicException) { continue; }
            if (cache == null) continue;
            foreach (var (entryKey, value) in cache)
            {
                if (value is not JsonObject entry || ParseEntryKey(entryKey) is not { } parts) continue;
                var token = (string?)entry["token"];
                if (string.IsNullOrEmpty(token)) continue;
                var expires = entry["expiresAt"]?.GetValue<double>() ?? 0;
                var cred = new DesktopCredential(token,
                    DateTimeOffset.FromUnixTimeMilliseconds((long)expires),
                    parts.Account, parts.Org, parts.Host,
                    (string?)entry["subscriptionType"], (string?)entry["rateLimitTier"]);
                candidates.Add((cred, parts.Scopes.Contains("user:inference"), rank));
            }
        }
        return candidates
            .OrderByDescending(c => !c.Cred.IsExpired)
            .ThenBy(c => c.CacheRank)
            .ThenByDescending(c => c.Inference)
            .ThenByDescending(c => c.Cred.ExpiresAt)
            .Select(c => c.Cred)
            .ToList();
    }

    /// <summary>Entry keys look like "acct:&lt;account&gt;|&lt;client&gt;:&lt;org&gt;:https://api.anthropic.com:&lt;scopes&gt;".</summary>
    static (string Account, string Org, string Host, string Scopes)? ParseEntryKey(string key)
    {
        if (!key.StartsWith("acct:")) return null;
        var hostStart = key.IndexOf(":https://", StringComparison.Ordinal);
        if (hostStart < 0) return null;
        var head = key[5..hostStart];
        var rest = key[(hostStart + 1)..];
        var scopeSep = rest.IndexOf(':', "https://".Length);
        var host = scopeSep < 0 ? rest : rest[..scopeSep];
        var scopes = scopeSep < 0 ? "" : rest[(scopeSep + 1)..];
        var orgSep = head.LastIndexOf(':');
        if (orgSep < 0) return null;
        var account = head[..orgSep].Split('|')[0];
        var org = head[(orgSep + 1)..];
        return (account, org, host, scopes);
    }

    /// <summary>Claude Desktop keeps its files open; read without blocking its writes.</summary>
    public static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    static byte[] DpapiUnprotect(byte[] data)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
            var output = new DataBlob();
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
                throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { handle.Free(); }
    }
}
