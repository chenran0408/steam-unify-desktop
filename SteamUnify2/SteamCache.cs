using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SteamUnify;

public sealed record CacheGame(int AppId, string Name, string Type, Dictionary<string, string> Localized, List<int> Genres, List<int> Tags);
public sealed record NameChange(string Name, Dictionary<string, string>? Localized);

/// <summary>Reads Steam's indexed binary KeyValues and plans complete replacement files in memory.</summary>
public static class SteamCache
{
    const uint V29 = 0x07564429, V30 = 0x0756442A;
    static readonly UTF8Encoding Utf8 = new(false, true);

    static string[] Files(string root, bool required = true)
    {
        root = Path.GetFullPath(root);
        var dir = Path.Combine(root, "appcache", "appinfo");
        if (Directory.Exists(dir))
        {
            var shards = Directory.GetFiles(dir, "appinfo_*.vdf");
            if (shards.Length != 0)
            {
                Array.Sort(shards, StringComparer.OrdinalIgnoreCase);
                return shards;
            }
        }
        foreach (var folder in new[] { "appcache", "steamapps" })
        {
            var path = Path.Combine(root, folder, "appinfo.vdf");
            if (File.Exists(path)) return [path];
        }
        if (!required) return [];
        throw new FileNotFoundException("未找到 Steam 游戏信息缓存。请先启动 Steam 并打开游戏库。");
    }

    internal sealed class Snapshot(string root, Dictionary<string, byte[]> contents)
    {
        internal string[] Paths()
        {
            var current = Files(root, required: false);
            // A different cache layout may have been published by Steam while it was closing.
            // Use it as a unit rather than mixing its indexes with old shards.
            if (current.Any(path => !contents.ContainsKey(path))) return current;
            return contents.Keys.ToArray();
        }

        internal byte[]? OriginalForMissing(string path) =>
            !File.Exists(path) && contents.TryGetValue(path, out var bytes) ? bytes : null;

        internal void RecoverMissing()
        {
            foreach (string path in Paths())
            {
                if (OriginalForMissing(path) is not { } bytes) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string staged = path + ".steamunify-recover-" + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        output.Write(bytes);
                        output.Flush(true);
                    }
                    File.Move(staged, path);
                }
                finally { if (File.Exists(staged)) File.Delete(staged); }
            }
        }
    }

    internal static Snapshot Capture(string root) => new(root, Files(root, required: false)
        .ToDictionary(path => path, CacheFile.ReadBytes, StringComparer.OrdinalIgnoreCase));

    public static List<CacheGame> Read(string steamRoot, List<string> warnings, IReadOnlySet<int>? appIds=null) =>
        Read(steamRoot, warnings, appIds, null);

    internal static List<CacheGame> Read(string steamRoot, List<string> warnings, IReadOnlySet<int>? appIds, Snapshot? snapshot)
    {
        var games = new Dictionary<int, CacheGame>();
        int readable = 0;
        foreach (var path in snapshot?.Paths() ?? Files(steamRoot))
        {
            try
            {
                var cache = snapshot?.OriginalForMissing(path) is { } prior
                    ? new CacheFile(prior, IsShard(path)) : CacheFile.Load(path);
                var parsed = new List<CacheGame>();
                foreach (var entry in cache.Entries)
                {
                    if(appIds!=null && !appIds.Contains(entry.Id))continue;
                    var info = cache.ParseGame(entry);
                    if (info.Game.Name.Length != 0) parsed.Add(info.Game);
                }
                foreach (var game in parsed) games[game.AppId] = game;
                readable++;
            }
            catch (Exception e) when (IsShard(path) && e is (IOException or InvalidDataException))
            {
                warnings.Add($"{Path.GetFileName(path)}：{e.Message}");
            }
        }
        if (readable == 0) throw new InvalidDataException("没有可读取的 Steam 缓存分片。" + warnings.LastOrDefault());
        return games.Values.ToList();
    }

    static bool IsShard(string path) => Path.GetFileName(path).StartsWith("appinfo_", StringComparison.OrdinalIgnoreCase);

    public static Dictionary<string, byte[]> PlanNames(string steamRoot, IReadOnlyDictionary<int, NameChange> changes,
        IDictionary<int, CacheGame>? changedOriginals = null)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (changes.Count == 0) return files;
        var found = new HashSet<int>();
        var warnings = new List<string>();
        foreach (var path in Files(steamRoot))
        {
            CacheFile cache;
            try { cache = CacheFile.Load(path); }
            catch (Exception e) when (IsShard(path) && e is (InvalidDataException or IOException))
            {
                warnings.Add($"{Path.GetFileName(path)}：{e.Message}");
                continue;
            }
            var patches = new Dictionary<int, byte[]>();
            foreach (var entry in cache.Entries)
            {
                if (!changes.TryGetValue(entry.Id, out var change)) continue;
                Require(found.Add(entry.Id), $"缓存中有重复的 AppID {entry.Id}，请重新扫描。");
                var original = cache.ParseGame(entry);
                var payload = cache.RewriteName(entry, change, original);
                if (payload != null)
                {
                    patches.Add(entry.Id, payload);
                    if (changedOriginals != null) changedOriginals[entry.Id] = original.Game;
                }
            }
            if (patches.Count == 0) continue;
            var result = cache.Rebuild(patches);
            var check = new CacheFile(result, IsShard(path));
            Require(cache.Entries.Select(e => e.Id).SequenceEqual(check.Entries.Select(e => e.Id)), "重建缓存时条目发生变化。");
            foreach (var entry in check.Entries.Where(e => patches.ContainsKey(e.Id)))
            {
                var game = check.ParseGame(entry).Game;
                var target = changes[entry.Id];
                Require(game.Name == target.Name && SameMap(game.Localized, target.Localized), $"AppID {entry.Id} 写入校验失败。");
            }
            files.Add(path, result);
        }
        if (found.Count != changes.Count)
        {
            var ids = string.Join(", ", changes.Keys.Except(found).Take(10));
            throw new InvalidDataException($"缓存中找不到 AppID {ids}。请重新扫描。" + warnings.FirstOrDefault());
        }
        return files;
    }

    static bool SameMap(Dictionary<string, string> current, Dictionary<string, string>? target) =>
        current.Count == (target?.Count ?? 0) &&
        current.All(pair => target!.TryGetValue(pair.Key, out var s) && s == pair.Value);

    static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidDataException(message);
    }

    static uint U32(byte[] data, int start) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(start, 4));

    static int Size(byte[] data, int start)
    {
        uint value = U32(data, start);
        Require(value <= int.MaxValue, "缓存记录长度超出支持范围。");
        return (int)value;
    }

    static void Set32(byte[] data, int start, int value) => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(start), value);

    readonly record struct Entry(int Id, int Header, int Vdf, int Size);
    readonly record struct Slice(int Offset, int Length);
    sealed class ParsedGame(int id)
    {
        public CacheGame Game = new(id, "", "", new(), new(), new());
        public Slice Name;
        public Slice Localized;
        public int CommonEnd = -1;
    }

    sealed class CacheFile
    {
        internal readonly List<Entry> Entries = new();
        readonly byte[] data;
        readonly bool sharded;
        readonly string[] keys;
        int tableStart, dataStart, entriesEnd, firstIndex, indexStride;

        internal static CacheFile Load(string path) => new(ReadBytes(path), IsShard(path));

        internal static byte[] ReadBytes(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Require(stream.Length <= int.MaxValue, "缓存文件过大。");
            byte[] bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }

        internal CacheFile(byte[] bytes, bool isShard)
        {
            data = bytes;
            sharded = isShard;
            Require(data.Length >= (sharded ? 44 : 16), "缓存文件头不完整。");
            uint magic = U32(data, 0);
            Require(magic == (sharded ? V30 : V29), $"不支持的缓存格式 0x{magic:x8}。");
            keys = sharded ? ReadShard() : ReadMonolith();
        }

        string[] Keys(int start, int end, int count)
        {
            Require(count <= end - start, "键名表超出文件末尾。");
            var result = new string[count];
            for (int i = 0; i < count; i++)
            {
                int zero = Array.IndexOf(data, (byte)0, start, end - start);
                Require(zero >= 0, "键名表中存在未结束的字符串。");
                result[i] = Encoding.UTF8.GetString(data, start, zero - start);
                start = zero + 1;
            }
            return result;
        }

        string[] ReadMonolith()
        {
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8));
            Require(offset >= 16 && offset <= (ulong)(data.Length - 4), "v29 键名表偏移无效。");
            tableStart = (int)offset;
            int pos = 16;
            while (tableStart - pos >= 8)
            {
                int id = unchecked((int)U32(data, pos)), size = Size(data, pos + 4);
                if (id == 0 && size == 0) break;
                Require(size >= 60 && size <= tableStart - pos - 8, $"AppID {id} 的记录长度无效。");
                Entries.Add(new Entry(id, pos, pos + 68, size - 60));
                pos += size + 8;
            }
            entriesEnd = pos;
            Require(!data.AsSpan(pos, tableStart - pos).ContainsAnyExcept((byte)0), "v29 条目区未正常结束。");
            return Keys(tableStart + 4, data.Length, Size(data, tableStart));
        }

        string[] ReadShard()
        {
            int count = Size(data, 16), indexBytes = Size(data, 20), keyBytes = Size(data, 28), blobBytes = Size(data, 32);
            Require(44L + indexBytes + keyBytes + blobBytes == data.Length, "分片长度与文件头不符。");
            tableStart = 44 + indexBytes;
            dataStart = tableStart + keyBytes;
            bool accepted = false;
            foreach (var (prefix, stride) in Layouts(count, indexBytes))
            {
                if (!ReadIndex(count, prefix, stride)) continue;
                firstIndex = 44 + prefix;
                indexStride = stride;
                accepted = true;
                break;
            }
            Require(accepted, $"无法识别分片索引布局（{count} 条，{indexBytes} 字节）。");
            return Keys(tableStart, dataStart, Size(data, 24));
        }

        static IEnumerable<(int Prefix, int Stride)> Layouts(int count, int length)
        {
            if (count == 0)
            {
                if (length == 0) yield return (0, 80);
                yield break;
            }
            // Newer builds add four index marker bytes or increase the record stride.
            foreach (var (prefix, suffix) in new[] { (0, 0), (4, 0), (0, 4), (4, 4) })
            {
                if (length == (long)count * 80 + prefix + suffix) yield return (prefix, 80);
            }
            foreach (var (prefix, suffix) in new[] { (0, 0), (4, 0), (0, 4), (4, 4) })
            {
                int recordsSize = length - prefix - suffix;
                if (recordsSize < 0 || recordsSize % count != 0) continue;
                int stride = recordsSize / count;
                if (stride is > 80 and <= 128) yield return (prefix, stride);
            }
        }

        bool ReadIndex(int count, int prefix, int stride)
        {
            Entries.Clear();
            int pos = 44 + prefix, offset = dataStart;
            for (int i = 0; i < count; i++, pos += stride)
            {
                uint length = U32(data, pos + 8);
                if (U32(data, pos) == 0 || U32(data, pos + 4) != offset - dataStart || length < 22 || length > data.Length - offset)
                    return false;
                Entries.Add(new Entry(unchecked((int)U32(data, pos)), pos, offset + 20, (int)length - 20));
                offset += (int)length;
            }
            return offset == data.Length;
        }

        internal ParsedGame ParseGame(Entry entry)
        {
            var parser = new GameParser(data.AsSpan(entry.Vdf, entry.Size), keys, entry.Id);
            return parser.Read();
        }

        internal byte[]? RewriteName(Entry entry, NameChange change, ParsedGame info)
        {
            Require(info.Name.Length > 0 && info.CommonEnd >= 0, $"AppID {entry.Id} 缺少可编辑的名称。");
            Require(!string.IsNullOrWhiteSpace(change.Name) && !change.Name.Contains('\0'), $"AppID {entry.Id} 的名称无效。");
            var edits = new List<(Slice Old, byte[] New)>(2);
            if (info.Game.Name != change.Name)
                edits.Add((info.Name, Utf8.GetBytes(change.Name + "\0")));
            bool wantLocalized = change.Localized?.Count > 0;
            if (!SameMap(info.Game.Localized, change.Localized) || (!wantLocalized && info.Localized.Length > 0))
            {
                byte[] localized = wantLocalized ? MakeLocalized(change.Localized!) : [];
                var slice = info.Localized.Length > 0 ? info.Localized : new Slice(info.CommonEnd, 0);
                edits.Add((slice, localized));
            }
            if (edits.Count == 0) return null;
            using var buffer = new MemoryStream();
            int cursor = 0;
            foreach (var edit in edits.OrderBy(e => e.Old.Offset))
            {
                buffer.Write(data, entry.Vdf + cursor, edit.Old.Offset - cursor);
                buffer.Write(edit.New);
                cursor = edit.Old.Offset + edit.Old.Length;
            }
            buffer.Write(data, entry.Vdf + cursor, entry.Size - cursor);
            return buffer.ToArray();
        }

        byte[] MakeLocalized(Dictionary<string, string> map)
        {
            int Key(string name)
            {
                int idx = Array.IndexOf(keys, name);
                Require(idx >= 0, $"当前缓存缺少本地化键名 {name}，无法恢复。");
                return idx;
            }
            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer, Encoding.UTF8, true);
            writer.Write((byte)0);
            writer.Write(Key("name_localized"));
            foreach (var pair in map)
            {
                Require(!pair.Value.Contains('\0'), "本地化名称中包含无效字符。");
                writer.Write((byte)1);
                writer.Write(Key(pair.Key));
                writer.Write(Utf8.GetBytes(pair.Value));
                writer.Write((byte)0);
            }
            writer.Write((byte)8);
            return buffer.ToArray();
        }

        internal byte[] Rebuild(IReadOnlyDictionary<int, byte[]> patches)
        {
            long total = data.Length + Entries.Where(e => patches.ContainsKey(e.Id)).Sum(e => (long)patches[e.Id].Length - e.Size);
            Require(total <= int.MaxValue, "修改后的缓存文件太大。");
            var output = new byte[(int)total];
            int pos = sharded ? dataStart : 16;
            Array.Copy(data, output, pos);
            foreach (var entry in Entries)
            {
                bool dirty = patches.TryGetValue(entry.Id, out var updated);
                ReadOnlySpan<byte> vdf = dirty ? updated : data.AsSpan(entry.Vdf, entry.Size);
                int headerSize = sharded ? 20 : 68;
                data.AsSpan(entry.Vdf - headerSize, headerSize).CopyTo(output.AsSpan(pos));
                if (sharded)
                {
                    Set32(output, entry.Header + 4, pos - dataStart);
                    Set32(output, entry.Header + 8, 20 + vdf.Length);
                }
                else Set32(output, pos + 4, 60 + vdf.Length);
                pos += headerSize;
                if (dirty) SHA1.HashData(vdf, output.AsSpan(pos - 20, 20));
                vdf.CopyTo(output.AsSpan(pos));
                pos += vdf.Length;
            }
            if (sharded)
            {
                Set32(output, 32, output.Length - dataStart);
                // Some layouts include marker bytes in their checksum, others cover records and keys only.
                bool fullIndex = U32(data, 36) == Crc32(data.AsSpan(44, dataStart - 44));
                uint indexCrc = fullIndex ? Crc32(output.AsSpan(44, dataStart - 44))
                    : Crc32(output.AsSpan(firstIndex, indexStride * Entries.Count), output.AsSpan(tableStart, dataStart - tableStart));
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(36), indexCrc);
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(40), Crc32(output.AsSpan(0, 40)));
            }
            else
            {
                data.AsSpan(entriesEnd).CopyTo(output.AsSpan(pos)); // Includes padding, byte-exact key table and trailing bytes.
                BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(8), pos + tableStart - entriesEnd);
            }
            return output;
        }
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        uint c = (uint)i;
        for (int bit = 0; bit < 8; bit++) c = (c >> 1) ^ ((c & 1) == 0 ? 0 : 0xedb88320u);
        return c;
    }).ToArray();

    static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second = default)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in first) crc = CrcTable[(byte)(crc ^ b)] ^ (crc >> 8);
        foreach (byte b in second) crc = CrcTable[(byte)(crc ^ b)] ^ (crc >> 8);
        return ~crc;
    }

    enum Section { None, Root, Common, Localized, Genres, Tags }

    /// <summary>Walks binary KeyValues directly. Offsets are relative to the VDF root.</summary>
    ref struct GameParser(ReadOnlySpan<byte> bytes, string[] keys, int id)
    {
        readonly ReadOnlySpan<byte> data = bytes;
        readonly string[] names = keys;
        readonly ParsedGame result = new(id);
        int pos;

        internal ParsedGame Read()
        {
            Require(data.Length >= 6 && data[0] == 0, "VDF 根节点无效。");
            Node(Section.None, 0);
            Require(pos == data.Length - 1 && data[pos] == 8, "VDF 结束标志无效。");
            return result;
        }

        void Need(int count) => Require(count <= data.Length - pos, "VDF 数据不完整。");

        void Node(Section parent, int depth)
        {
            Require(depth < 256, "VDF 嵌套层级过多。");
            Need(5);
            int start = pos;
            byte type = data[pos++];
            uint keyIndex = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
            pos += 4;
            string key = parent == Section.None || keyIndex >= names.Length ? "" : names[keyIndex];
            if (type == 0)
            {
                var section = depth == 0 ? Section.Root : (parent, key) switch
                {
                    (Section.Root, "common") => Section.Common,
                    (Section.Common, "name_localized") => Section.Localized,
                    (Section.Common, "genres") => Section.Genres,
                    (Section.Common, "store_tags") => Section.Tags,
                    _ => Section.None
                };
                while (true)
                {
                    Need(1);
                    if (data[pos] == 8) break;
                    Node(section, depth + 1);
                }
                if (section == Section.Common) result.CommonEnd = pos;
                pos++;
                if (section == Section.Localized) result.Localized = new Slice(start, pos - start);
                return;
            }
            if (type == 1)
            {
                int len = data[pos..].IndexOf((byte)0);
                Require(len >= 0, "VDF 字符串未结束。");
                var value = data.Slice(pos, len);
                if (parent == Section.Common && key == "name")
                {
                    result.Game = result.Game with { Name = Encoding.UTF8.GetString(value) };
                    result.Name = new Slice(pos, len + 1);
                }
                else if (parent == Section.Common && key == "type")
                    result.Game = result.Game with { Type = Encoding.UTF8.GetString(value) };
                else if (parent == Section.Localized)
                    result.Game.Localized[key] = Encoding.UTF8.GetString(value);
                else if (parent is Section.Genres or Section.Tags &&
                         int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out int number))
                    AddNumber(parent, number);
                pos += len + 1;
                return;
            }
            if (type == 5)
            {
                do
                {
                    Need(2);
                    pos += 2;
                } while (data[pos - 2] != 0 || data[pos - 1] != 0);
                return;
            }
            int size = type switch
            {
                2 or 3 or 4 or 6 => 4,
                7 or 10 => 8,
                _ => throw new InvalidDataException($"不支持的 VDF 数据类型 0x{type:x2}。")
            };
            Need(size);
            if (type == 2 && parent is Section.Genres or Section.Tags)
                AddNumber(parent, BinaryPrimitives.ReadInt32LittleEndian(data[pos..]));
            pos += size;
        }

        void AddNumber(Section section, int number)
        {
            (section == Section.Genres ? result.Game.Genres : result.Game.Tags).Add(number);
        }
    }
}
