using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CUE4Parse.FileProvider.Objects;
using FModel.ViewModels;
using K4os.Compression.LZ4.Streams;
using Newtonsoft.Json.Linq;

namespace FModel.Automation;

internal static class IndexBackups
{
    private sealed record Entry(string Path, long Size, bool Encrypted);

    public static object Create(IEnumerable<GameFile> files, string path, CancellationToken ct)
    {
        path = System.IO.Path.GetFullPath(path);
        // Provider enumeration is ordered by mount priority and may contain patched versions of the same path.
        var entries = files.Where(x => !x.IsUePackagePayload).DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var compressed = LZ4Stream.Encode(stream);
        using var writer = new BinaryWriter(compressed);
        writer.Write(BackupManagerViewModel.FBKP_MAGIC);
        writer.Write((byte) EBackupVersion.Latest);
        writer.Write(entries.Length);
        foreach (var file in entries)
        {
            ct.ThrowIfCancellationRequested();
            writer.Write(file.Size);
            writer.Write(file.IsEncrypted);
            writer.Write(file.Path);
        }
        return new { path = path.Replace('\\', '/'), files = entries.Length, coverage = "File paths, sizes and encryption flags; not asset payloads or content hashes." };
    }

    public static object Compare(IEnumerable<GameFile> files, JObject args, CancellationToken ct)
    {
        using var stream = File.OpenRead(args.Value<string>("backupPath"));
        using var compressed = LZ4Stream.Decode(stream);
        using var reader = new BinaryReader(compressed);
        if (reader.ReadUInt32() != BackupManagerViewModel.FBKP_MAGIC) throw new NotSupportedException("This operation requires a versioned FModel backup.");
        var version = (EBackupVersion) reader.ReadByte();
        if (version < EBackupVersion.Initial || version > EBackupVersion.Latest) throw new NotSupportedException("Unsupported backup version.");
        var count = reader.ReadInt32();
        if (count < 0) throw new InvalidDataException("Invalid backup entry count.");
        var previous = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var size = reader.ReadInt64();
            var encrypted = reader.ReadBoolean();
            var path = QueryHelpers.NormalizePath(reader.ReadString());
            previous.TryAdd(path, new Entry(path, size, encrypted));
        }
        var changes = new List<object>();
        foreach (var file in files.Where(x => !x.IsUePackagePayload).DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!previous.Remove(file.Path, out var old)) changes.Add(new { change = "added", path = file.Path, size = file.Size });
            else if (old.Size != file.Size || old.Encrypted != file.IsEncrypted)
                changes.Add(new { change = "modified", path = file.Path, oldSize = old.Size, size = file.Size, encrypted = file.IsEncrypted });
        }
        foreach (var removed in previous.Values) changes.Add(new { change = "removed", path = removed.Path, oldSize = removed.Size });
        return new { coverage = "Path, size and encryption changes only; equal-size payload changes are not detected.", results = QueryHelpers.Page(changes, args) };
    }
}
