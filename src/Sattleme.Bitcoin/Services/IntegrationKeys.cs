using System.Security.Cryptography;
using Smartstore.Core.Configuration;
using Smartstore.Core.Data;
using Smartstore.Core.Security;

namespace Sattleme.Bitcoin.Services;

/// <summary>Whether a scope of the configuration (every store, or one) has an integration key.</summary>
public enum KeyStatus
{
    /// <summary>No key, neither the store's own nor every store's.</summary>
    None,

    /// <summary>Every store's key (the scope is every store).</summary>
    Saved,

    /// <summary>The store's own key.</summary>
    SavedForStore,

    /// <summary>The store has none of its own: every store's key applies.</summary>
    SavedForAll,

    /// <summary>A key is saved, and cannot be read: encrypted with another encryption key, or damaged.</summary>
    Unreadable,
}

/// <summary>
/// The integration key at rest: encrypted with Smartstore's encryptor (IEncryptor: TripleDES, keyed by
/// the store's encryption key, SecuritySettings.EncryptionKey) in Smartstore's settings store, one
/// setting per store scope, decrypted only to make the SDK's client of a store. It never leaves the
/// server: no page, no model, no log.
/// </summary>
public class IntegrationKeys
{
    /// <summary>What every integration key starts with.</summary>
    public const string KeyPrefix = "sattleme_ck_";

    private readonly SmartDbContext _db;
    private readonly IEncryptor _encryptor;
    private readonly ISettingService _settingService;

    /// <summary>The keys, encrypted with <paramref name="encryptor"/>.</summary>
    public IntegrationKeys(SmartDbContext db, IEncryptor encryptor, ISettingService settingService)
    {
        _db = db;
        _encryptor = encryptor;
        _settingService = settingService;
    }

    /// <summary>The key set for the store <paramref name="storeId"/> (its own, else every store's), or null.</summary>
    public virtual async Task<string?> KeyOfStoreAsync(int storeId)
    {
        var stored = await _settingService.GetSettingByKeyAsync<string>(SattlemeDefaults.KeySetting, "", storeId, doFallback: true);
        return Unprotect(stored);
    }

    /// <summary>Whether the scope <paramref name="storeScope"/> (0: every store) has a key that can be read.</summary>
    public virtual async Task<KeyStatus> StatusAsync(int storeScope)
    {
        var own = await _settingService.GetSettingByKeyAsync<string>(SattlemeDefaults.KeySetting, "", storeScope);
        if (!string.IsNullOrEmpty(own))
        {
            return Unprotect(own) is null ? KeyStatus.Unreadable : storeScope == 0 ? KeyStatus.Saved : KeyStatus.SavedForStore;
        }
        if (storeScope == 0)
        {
            return KeyStatus.None;
        }
        var shared = await _settingService.GetSettingByKeyAsync<string>(SattlemeDefaults.KeySetting, "", 0);
        return string.IsNullOrEmpty(shared) ? KeyStatus.None : Unprotect(shared) is null ? KeyStatus.Unreadable : KeyStatus.SavedForAll;
    }

    /// <summary>Saves <paramref name="key"/>, encrypted, for the scope <paramref name="storeScope"/> (0: every store).</summary>
    public virtual async Task SaveAsync(string key, int storeScope)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        await _settingService.ApplySettingAsync(SattlemeDefaults.KeySetting, _encryptor.EncryptText(key), storeScope);
        await _db.SaveChangesAsync();
    }

    /// <summary>Removes the key of the scope <paramref name="storeScope"/>: a store then has every store's key, if any.</summary>
    public virtual async Task RemoveAsync(int storeScope)
    {
        if (await _settingService.RemoveSettingAsync(SattlemeDefaults.KeySetting, storeScope))
        {
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// <paramref name="stored"/> decrypted with the store's encryption key, or null when it is not an
    /// encrypted integration key (another encryption key made it, or it is damaged): the admin enters
    /// it again.
    /// </summary>
    public virtual string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }
        try
        {
            var key = _encryptor.DecryptText(stored);
            return key is not null && key.StartsWith(KeyPrefix, StringComparison.Ordinal) ? key : null;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}
