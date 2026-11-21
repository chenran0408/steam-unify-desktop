using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace SteamUnify;

public sealed record SteamLocation(string Root, string Account);
public sealed record LibraryInfo(HashSet<int> Owned, Dictionary<int,string> Installed, Dictionary<int,string> TagNames);
public sealed record LibraryEdit(int AppId, string? Name, string? Collection);
public sealed record RecommendedEdit(LibraryEdit Edit, string CurrentName, IReadOnlyList<string> CurrentGroups);
public sealed class StaleRecommendationException(string message) : InvalidOperationException(message);
public static partial class SteamLibrary
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static string Canon(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static string RootKey(string root) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canon(root).ToUpperInvariant())))[..16];
    internal static string OwnershipPath(SteamLocation location, string data) =>
        Path.Combine(Canon(data), $"collections-{RootKey(location.Root)}-{location.Account}.json");

    public static SteamLocation Detect(string? preferredRoot, string? preferredAccount)
    {
        var candidates = !string.IsNullOrWhiteSpace(preferredRoot) ? new[] { preferredRoot } : new[]
        {
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string,
            Path.GetDirectoryName((Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null) as string) ?? ""),
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
            @"D:\Steam", @"E:\Steam"
        };
        string? root = candidates.Where(p => !string.IsNullOrEmpty(p)).Select(p => Canon(p!))
            .FirstOrDefault(p => Directory.Exists(Path.Combine(p, "userdata")));
        if (root == null) throw new DirectoryNotFoundException("找不到 Steam 安装目录。请在设置中选择包含 userdata 的 Steam 文件夹。");
        var accounts = Directory.EnumerateDirectories(Path.Combine(root, "userdata"))
            .Select(Path.GetFileName).OfType<string>()
            .Where(a => uint.TryParse(a, out uint account) && account > 0)
            .OrderByDescending(a => File.GetLastWriteTimeUtc(Path.Combine(root, "userdata", a, "config", "localconfig.vdf"))).ToArray();
        if (!string.IsNullOrWhiteSpace(preferredAccount))
        {
            if (!accounts.Contains(preferredAccount)) throw new InvalidDataException("所选账号不存在于这个 Steam 目录。");
            return new(root, preferredAccount);
        }
        var registered = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (!string.IsNullOrEmpty(registered) && Paths.Equals(root,Canon(registered)))
        {
            var rawActive = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null);
            string? active = rawActive is int n ? unchecked((uint)n).ToString() : rawActive?.ToString();
            if (active != null && accounts.Contains(active)) return new(root,active);
        }
        var logins = SteamVdf.Open(Path.Combine(root,"config","loginusers.vdf"))?["users"];
        if (logins != null)
        {
            foreach (var (id,user) in logins.Items)
            {
                if (user["MostRecent"]?.Text != "1" || !ulong.TryParse(id,out ulong steamId)) continue;
                string recent = ((uint)(steamId & uint.MaxValue)).ToString();
                if (accounts.Contains(recent)) return new(root,recent);
            }
        }
        string accountId = accounts.FirstOrDefault()
            ?? throw new InvalidDataException("此 Steam 客户端还没有可用账号，请先登录 Steam。");
        return new(root, accountId);
    }

    private static IEnumerable<string> Libraries(string root)
    {
        var paths = new HashSet<string>(Paths) { Canon(root) };
        var folders = SteamVdf.Open(Path.Combine(root, "steamapps", "libraryfolders.vdf"))?["libraryfolders"];
        if (folders != null)
            foreach (var (key,node) in folders.Items)
            {
                string? path = node["path"]?.Text ?? node.Text;
                if (int.TryParse(key, out _) && !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
                    paths.Add(Canon(path));
            }
        return paths;
    }

    private static Dictionary<int,string> FindInstalled(string root)
    {
        var installed = new Dictionary<int,string>();
        foreach (var library in Libraries(root))
        {
            string dir = Path.Combine(library,"steamapps");
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Directory.EnumerateFiles(dir,"appmanifest_*.acf"))
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                if (int.TryParse(stem["appmanifest_".Length..], out int appId)) installed.TryAdd(appId, path);
            }
        }
        return installed;
    }

    public static LibraryInfo Inspect(SteamLocation location)
    {
        var local = SteamVdf.Open(Path.Combine(location.Root,"userdata",location.Account,"config","localconfig.vdf"))?["UserLocalConfigStore"];
        var apps = local?["Software"]?["Valve"]?["Steam"]?["apps"];
        var owned = new HashSet<int>();
        if (apps != null) foreach (var (key,_) in apps.Items) if (int.TryParse(key, out var id)) owned.Add(id);
        var tags = new Dictionary<int,string>();
        foreach (var key in new[]{"LocalizedTagNames_schinese","LocalizedTagNames2_schinese"})
        {
            string? raw = local?["WebStorage"]?[key]?.Text;
            if (string.IsNullOrEmpty(raw)) continue;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("tags", out var table) || table.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in table.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("tagid", out var id)
                        && int.TryParse(id.ToString(), out int tagId) && entry.TryGetProperty("name", out var name)
                        && name.ValueKind == JsonValueKind.String) tags[tagId] = name.GetString()!;
            }
            catch (JsonException) { /* Steam can leave either optional web cache entry incomplete. */ }
        }
        return new(owned,FindInstalled(location.Root),tags);
    }

    public static Dictionary<int,List<string>> Memberships(SteamLocation location, string appData) =>
        new SteamCollections(location,appData).Memberships();

    public static string Apply(SteamLocation location, IReadOnlyList<LibraryEdit> edits, string appData, bool restartSteam,
        Action<string,int,int>? progress=null)
    {
        if (edits.Count == 0) return "";
        SteamCache.Snapshot? snapshot = null;
        return WithClosedClient(location,restartSteam,() =>
        {
            snapshot?.RecoverMissing();
            return ApplyClosed(location,edits,appData,progress);
        }, () =>
        {
            if (edits.Any(edit => edit.Name != null)) snapshot = CaptureCache(location,progress);
            progress?.Invoke("等待 Steam 退出",0,0);
        });
    }

    public static string ApplyRecommended(SteamLocation location,string appData,bool restartSteam,IReadOnlyList<RecommendedEdit> edits,
        bool autoAccount,Action beforeClientShutdown,Func<IReadOnlyList<RecommendedEdit>> readFreshPlan,Action<string,int,int>? progress=null) =>
        ApplyRecommended(location,appData,restartSteam,edits,autoAccount,beforeClientShutdown,_ => readFreshPlan(),progress);

    internal static string ApplyRecommended(SteamLocation location,string appData,bool restartSteam,IReadOnlyList<RecommendedEdit> edits,
        bool autoAccount,Action beforeClientShutdown,Func<SteamCache.Snapshot?,IReadOnlyList<RecommendedEdit>> readFreshPlan,
        Action<string,int,int>? progress=null)
    {
        if (edits.Count == 0) return "";
        var shown = edits.ToDictionary(row=>row.Edit.AppId,row=>row with { CurrentGroups=row.CurrentGroups.ToArray() });
        SteamCache.Snapshot? snapshot = null;
        void VerifyAccount()
        {
            if (!Directory.Exists(Path.Combine(location.Root,"userdata",location.Account)) ||
                (autoAccount && Detect(location.Root,null).Account != location.Account))
                throw new StaleRecommendationException("Steam 账号已更换，请查看刷新后的整理建议。");
        }
        return WithClosedClient(location, restartSteam, () =>
        {
            VerifyAccount();
            progress?.Invoke("核对游戏库",0,0);
            var fresh = readFreshPlan(snapshot);
            const string changed = "游戏库或整理建议已变化，请查看刷新后的内容。";
            if (fresh.Count != shown.Count) throw new StaleRecommendationException(changed);
            foreach (var row in fresh)
            {
                if (!shown.Remove(row.Edit.AppId,out var earlier) || row.Edit != earlier.Edit || row.CurrentName != earlier.CurrentName ||
                    !earlier.CurrentGroups.ToHashSet(StringComparer.Ordinal).SetEquals(row.CurrentGroups))
                    throw new StaleRecommendationException(changed);
            }
            VerifyAccount();
            if (fresh.Any(row => row.Edit.Name != null)) snapshot?.RecoverMissing();
            return ApplyClosed(location,fresh.Select(row=>row.Edit).ToList(),appData,progress);
        }, () =>
        {
            VerifyAccount();
            beforeClientShutdown();
            VerifyAccount();
            snapshot = CaptureCache(location,progress);
            progress?.Invoke("等待 Steam 退出",0,0);
        });
    }

    private static SteamCache.Snapshot CaptureCache(SteamLocation location,Action<string,int,int>? progress)
    {
        progress?.Invoke("正在保留 Steam 缓存",0,0);
        return SteamCache.Capture(location.Root);
    }

    private static string ApplyClosed(SteamLocation location,IReadOnlyList<LibraryEdit> edits,string appData,Action<string,int,int>? progress)
    {
        progress?.Invoke("准备更改",0,0);
        var writes = new Dictionary<string,byte[]?>(Paths);
        var names = edits.Where(e => e.Name != null).ToDictionary(e => e.AppId,e => new NameChange(e.Name!.Trim(), null));
        if (names.Values.Any(n => string.IsNullOrWhiteSpace(n.Name) || n.Name.IndexOfAny(new[]{'\0','\n','\r'}) >= 0))
            throw new InvalidDataException("游戏名称不能为空或包含换行。");
        if (names.Count > 0) PlanTrackedNames(location, appData, names, writes);
        if (edits.Any(e => e.Collection != null)) new SteamCollections(location,appData).Plan(edits,writes);
        return Commit(location,appData,writes,progress:progress);
    }

    static T WithClosedClient<T>(SteamLocation location, bool restart, Func<T> work, Action? beforeClientShutdown = null)
    {
        if (!uint.TryParse(location.Account, out uint account) || account == 0 || location.Account != account.ToString())
            throw new InvalidDataException("Steam 账号目录无效。");
        string exe = Path.Combine(Canon(location.Root), "steam.exe");
        using var gate = new Mutex(false, @"Local\SteamUnifyWrite-" + RootKey(location.Root));
        try
        {
            if (!gate.WaitOne(0)) throw new IOException("另一个 Steam 统译实例正在写入，请稍后再试。");
        }
        catch (AbandonedMutexException) { /* The previous writer exited. Its backup is available on disk. */ }
        try
        {
            bool running = IsRunning(exe);
            if (running)
            {
                beforeClientShutdown?.Invoke();
                using var request = Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = location.Root });
                var timer = Stopwatch.StartNew();
                do
                {
                    Thread.Sleep(300);
                    if (!IsRunning(exe)) break;
                    if (timer.Elapsed.TotalSeconds >= 75) throw new IOException("等待 Steam 退出超时，请手动关闭 Steam 后重试。");
                } while (true);
            }
            try { return work(); }
            finally
            {
                if (running && restart && !IsRunning(exe))
                    Process.Start(new ProcessStartInfo(exe){UseShellExecute=true,WorkingDirectory=location.Root})?.Dispose();
            }
        }
        finally { gate.ReleaseMutex(); }
    }

    private static bool IsRunning(string exe)
    {
        bool found = false;
        foreach (var proc in Process.GetProcessesByName("steam"))
        {
            using (proc)
            {
                try { if (Paths.Equals(proc.MainModule?.FileName,exe)) found = true; }
                catch (InvalidOperationException) when (proc.HasExited) { }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    if (!proc.HasExited) throw new IOException("无法识别正在运行的 Steam。请先退出 Steam。",ex);
                }
            }
        }
        return found;
    }
}
