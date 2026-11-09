namespace HelloOtp.Core;

/// <summary>One file from the shared folder. Unreadable files have empty content.</summary>
public sealed record SyncFile(string Name, byte[] Content);

/// <summary>What a sync needs written. A null file is already up to date.</summary>
public sealed record SyncOutcome(byte[]? Local, byte[]? Own, int Unreadable);

/// <summary>
/// Merges vaults. Each account keeps its latest version and a deletion beats anything older, so
/// merging in any order gives the same result, and an old copy of a file can never undo a change.
/// </summary>
public static class VaultSync
{
    /// <summary>Merges source into target and reports whether target changed. Source is not altered.</summary>
    public static bool Merge(UnlockedVault target, UnlockedVault source)
    {
        var changed = false;
        foreach (var tombstone in source.Tombstones)
        {
            var existing = FindTombstone(target, tombstone.Id);
            if (existing == null)
            {
                target.Tombstones.Add(new Tombstone((byte[])tombstone.Id.Clone(), tombstone.DeletedAt));
                changed = true;
            }
            else if (tombstone.DeletedAt > existing.DeletedAt)
            {
                existing.DeletedAt = tombstone.DeletedAt;
                changed = true;
            }
        }

        foreach (var account in target.Accounts.Where(account => IsDeleted(target, account)).ToList())
        {
            target.Accounts.Remove(account);
            account.Dispose();
            changed = true;
        }

        foreach (var incoming in source.Accounts)
        {
            if (IsDeleted(target, incoming))
                continue;

            var existing = target.Accounts.FirstOrDefault(account => account.HasSameSecretAs(incoming));
            if (existing == null)
            {
                target.Accounts.Add(new OtpAccount(
                    incoming.Issuer, incoming.Label, SecretBuffer.CopyOf(incoming.Secret),
                    incoming.Algorithm, incoming.Digits, incoming.Period) { ModifiedAt = incoming.ModifiedAt });
                changed = true;
            }
            else if (IsNewer(incoming, existing))
            {
                existing.Issuer = incoming.Issuer;
                existing.Label = incoming.Label;
                existing.ModifiedAt = incoming.ModifiedAt;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Merges this PC's saved vault and every readable shared file into the vault, and returns what must be written.</summary>
    public static SyncOutcome Apply(UnlockedVault vault, ReadOnlySpan<byte> localFile, IReadOnlyList<SyncFile> files, string ownName)
    {
        var key = vault.CopyKey();
        UnlockedVault? own = null;
        try
        {
            // The saved vault may be newer than this copy if it changed during the folder read.
            using var local = Vault.OpenWithKey(localFile, key);
            Merge(vault, local);

            var unreadable = 0;
            foreach (var file in files)
            {
                UnlockedVault other;
                try
                {
                    other = Vault.OpenWithKey(file.Content, key);
                }
                catch (VaultException)
                {
                    unreadable++;
                    continue;
                }

                Merge(vault, other);
                if (file.Name == ownName)
                    own = other;
                else
                    other.Dispose();
            }

            var localChanged = Merge(local, vault);
            var ownChanged = own == null || Merge(own, vault);
            return new SyncOutcome(localChanged ? vault.ToFileBytes() : null, ownChanged ? vault.ToSyncBytes() : null, unreadable);
        }
        finally
        {
            own?.Dispose();
            SecretBuffer.Wipe(key);
        }
    }

    /// <summary>Whether any of the files belongs to the same vault, meaning no recovery code is needed to join.</summary>
    public static bool AnyOpensWith(UnlockedVault vault, IEnumerable<SyncFile> files)
    {
        var key = vault.CopyKey();
        try
        {
            foreach (var file in files)
            {
                try
                {
                    Vault.OpenWithKey(file.Content, key).Dispose();
                    return true;
                }
                catch (VaultException)
                {
                }
            }
            return false;
        }
        finally
        {
            SecretBuffer.Wipe(key);
        }
    }

    /// <summary>Opens the first file the recovery code fits, or returns null.</summary>
    public static UnlockedVault? OpenWithRecovery(IEnumerable<SyncFile> files, ReadOnlySpan<byte> recoveryCode)
    {
        foreach (var file in files)
        {
            try
            {
                return Vault.OpenWithRecovery(file.Content, recoveryCode);
            }
            catch (VaultException)
            {
            }
        }
        return null;
    }

    private static Tombstone? FindTombstone(UnlockedVault vault, byte[] id) =>
        vault.Tombstones.FirstOrDefault(tombstone => tombstone.Id.AsSpan().SequenceEqual(id));

    /// <summary>On a tie the account stays, because losing a secret is worse than keeping a deleted one.</summary>
    private static bool IsDeleted(UnlockedVault vault, OtpAccount account) =>
        vault.Tombstones.Count > 0 && FindTombstone(vault, account.Identity())?.DeletedAt > account.ModifiedAt;

    /// <summary>Ties break on the names so every PC picks the same winner.</summary>
    private static bool IsNewer(OtpAccount candidate, OtpAccount current)
    {
        if (candidate.ModifiedAt != current.ModifiedAt)
            return candidate.ModifiedAt > current.ModifiedAt;
        var issuer = string.CompareOrdinal(candidate.Issuer, current.Issuer);
        return issuer != 0 ? issuer > 0 : string.CompareOrdinal(candidate.Label, current.Label) > 0;
    }
}

/// <summary>Plain file access to the shared folder. Each PC writes only its own file, so cloud folders never see two PCs edit one file.</summary>
public static class SyncFolder
{
    public const string Extension = ".hotp";
    private const int MaxFileSize = 4 * 1024 * 1024;

    public static List<SyncFile> ReadAll(string folder)
    {
        var files = new List<SyncFile>();
        foreach (var path in Directory.EnumerateFiles(folder, "*" + Extension))
        {
            byte[] content;
            try
            {
                content = new FileInfo(path).Length > MaxFileSize ? Array.Empty<byte>() : File.ReadAllBytes(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                content = Array.Empty<byte>();
            }
            files.Add(new SyncFile(Path.GetFileName(path), content));
        }
        return files;
    }

    /// <summary>Replaces the file in one step, so another PC never reads half of it.</summary>
    public static void Write(string folder, string name, byte[] content)
    {
        var path = Path.Combine(folder, name);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    public static void Delete(string folder, string name) => File.Delete(Path.Combine(folder, name));

    /// <summary>A file name for this PC. The random part keeps two PCs with the same name apart.</summary>
    public static string NewFileName(string machineName)
    {
        var safe = new string(machineName.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        return $"{safe}-{Guid.NewGuid().ToString("N")[..8]}{Extension}";
    }
}
