using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MVZ2ModManager.Core;

/// <summary>一份关卡存档里出现的一条「需要 &lt;命名空间&gt; @ &lt;版本&gt;」。</summary>
internal sealed record SaveRef(string FilePath, string Namespace, int DataVersion);

/// <summary>某个命名空间在所有存档里的使用汇总。</summary>
internal sealed record SaveUsage(string Namespace, int MinVersion, int MaxVersion, int SaveCount, List<string> Samples)
{
    public string VersionRange => MinVersion == MaxVersion ? MinVersion.ToString() : $"{MinVersion}–{MaxVersion}";
}

/// <summary>「这些存档需要一个当前没启用的命名空间」的警告。</summary>
internal sealed record SaveWarning(string Namespace, string Owner, int SaveCount, string VersionRange, List<string> Samples);

/// <summary>
/// 存档兼容性检查：读关卡存档头，判断"关掉某个模组会不会让已有存档读不进去"。
///
/// <para><b>存档格式</b>：<c>.lvl</c> 是 <b>gzip</b>，
/// 解开后的第一个 JSON 对象就是 <c>SerializableLevelControllerHeader</c>：</para>
/// <code>
/// { "_t" : "SerializableLevelControllerHeader",
///   "identifiers" : { "identifiers" : [
///       { "spaceName" : "mvz2_lab",      "dataVersion" : 4 },
///       { "spaceName" : "modular_curse", "dataVersion" : 4 },
///       { "spaceName" : "mvz2",          "dataVersion" : 5 } ] } }
/// </code>
///
/// <para>游戏读档时按**命名空间逐个比对**（<c>LevelDataIdentifierList.Compare</c>）：
/// 存档里记着 <c>mvz2_lab@4</c>，而这次启动没有 <c>mvz2_lab</c> 这个 <c>ModInfo</c>，
/// 这份存档就会被判"版本不匹配"而**读不进去**。所以禁用模组前必须先查一遍这里。</para>
///
/// <para>注意同一个命名空间会出现多个 <c>dataVersion</c>（实测 <c>mvz2</c> 有 3/4/5）——
/// 它是**保存那一刻**写死的，不是当前值，所以比对必须 namespace + version 双匹配。</para>
/// </summary>
internal static class SaveCompatibility
{
    /// <summary>存档根的上层：<c>%USERPROFILE%\AppData\LocalLow\Cuerzor\</c>。</summary>
    public const string CompanyName = "Cuerzor";

    /// <summary>调试版与正式版的 LocalLow 产品名（两个都查）。</summary>
    private static readonly string[] ProductNames = { "MinecraftVSZombies2Test", "MinecraftVSZombies2" };

    private const int HeaderProbeBytes = 64 * 1024;

    // 扫描结果缓存：面板刷新很频繁，而存档不会一秒钟变一次。
    private static List<SaveRef>? _cache;
    private static DateTime _cacheAt;

    /// <summary>可能的存档根目录（存在才返回）。</summary>
    public static IEnumerable<string> UserDataRoots()
    {
        string localLow = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", CompanyName);

        foreach (var product in ProductNames)
        {
            string root = Path.Combine(localLow, product, "userdata");
            if (Directory.Exists(root)) yield return root;
        }
    }

    public static bool HasAnyUserData => UserDataRoots().Any();

    /// <summary>丢掉扫描缓存。换了游戏安装之后必须调一次 —— 否则会拿着上一套安装的存档结论。</summary>
    public static void InvalidateCache() => _cache = null;

    /// <summary>扫描所有存档（带 30 秒缓存）。</summary>
    public static List<SaveRef> ScanAll(bool force = false)
    {
        if (!force && _cache != null && DateTime.UtcNow - _cacheAt < TimeSpan.FromSeconds(30))
            return _cache;

        var refs = new List<SaveRef>();

        foreach (var root in UserDataRoots())
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.lvl", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var file in files)
            {
                if (!TryReadIdentifiers(file, out var ids)) continue;
                foreach (var (ns, ver) in ids)
                    refs.Add(new SaveRef(file, ns, ver));
            }
        }

        _cache = refs;
        _cacheAt = DateTime.UtcNow;
        return refs;
    }

    /// <summary>按命名空间汇总。</summary>
    public static List<SaveUsage> Summarize(IEnumerable<SaveRef> refs)
    {
        return refs
            .GroupBy(r => r.Namespace, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var files = g.Select(r => r.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return new SaveUsage(
                    g.Key,
                    g.Min(r => r.DataVersion),
                    g.Max(r => r.DataVersion),
                    files.Count,
                    files.Select(Path.GetFileName).OfType<string>().Take(5).ToList());
            })
            .OrderByDescending(u => u.SaveCount)
            .ToList();
    }

    /// <summary>
    /// 存档需要、但当前**没有启用**的命名空间。
    /// 排除原版 <c>mvz2</c>（它永远在），以及压根不属于任何已安装模组的命名空间
    /// （那种情况提示"你缺这个模组"更有用）。
    /// </summary>
    public static List<SaveWarning> RequiredButDisabled(
        IReadOnlyList<SaveUsage> usages,
        IReadOnlyDictionary<string, string> owners,
        ISet<string> enabledNamespaces)
    {
        var warnings = new List<SaveWarning>();

        foreach (var u in usages)
        {
            if (string.Equals(u.Namespace, Mvz2Catalog.VanillaNamespace, StringComparison.OrdinalIgnoreCase)) continue;
            if (enabledNamespaces.Contains(u.Namespace)) continue;

            owners.TryGetValue(u.Namespace, out var owner);
            warnings.Add(new SaveWarning(
                u.Namespace,
                owner ?? "（未安装）",
                u.SaveCount,
                u.VersionRange,
                u.Samples));
        }

        return warnings;
    }

    /// <summary>
    /// 读一份 <c>.lvl</c> 的存档头，取出「需要哪些命名空间 @ 版本」。
    /// 不是 gzip / 结构不认识 / 文件损坏都返回 false，绝不上抛 —— 存档目录里
    /// 可能有别的工具留下的文件。
    /// </summary>
    public static bool TryReadIdentifiers(string lvlPath, out List<(string Namespace, int Version)> identifiers)
    {
        identifiers = new List<(string, int)>();

        try
        {
            using var fs = File.OpenRead(lvlPath);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);

            var buf = new byte[HeaderProbeBytes];
            int total = 0, read;
            while (total < buf.Length && (read = gz.Read(buf, total, buf.Length - total)) > 0)
                total += read;
            if (total == 0) return false;

            string head = Encoding.UTF8.GetString(buf, 0, total);
            string? json = FirstJsonObject(head);
            if (json == null) return false;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("identifiers", out var wrapper)) return false;
            if (!wrapper.TryGetProperty("identifiers", out var list) || list.ValueKind != JsonValueKind.Array) return false;

            foreach (var entry in list.EnumerateArray())
            {
                string ns = entry.TryGetProperty("spaceName", out var s) ? s.GetString() ?? "" : "";
                int ver = entry.TryGetProperty("dataVersion", out var v) && v.TryGetInt32(out int iv) ? iv : -1;
                if (ns.Length > 0) identifiers.Add((ns, ver));
            }

            return identifiers.Count > 0;
        }
        catch
        {
            identifiers.Clear();
            return false;
        }
    }

    /// <summary>
    /// 取出字符串里**第一个完整的花括号对象**（认得字符串与转义，所以内部的花括号不算数）。
    /// 存档头一定在最前面，不需要解析整个文件。
    /// </summary>
    private static string? FirstJsonObject(string text)
    {
        int start = text.IndexOf('{');
        if (start < 0) return null;

        int depth = 0;
        bool inString = false, escaped = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return text[start..(i + 1)];
                    break;
            }
        }

        return null;   // 64KB 内没读完 → 结构不是我们认识的
    }
}
