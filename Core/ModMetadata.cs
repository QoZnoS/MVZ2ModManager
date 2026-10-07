using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace MVZ2ModManager.Core;

/// <summary>
/// 直接从 DLL 的 .NET 元数据里读 BepInEx 的插件声明 —— <b>不加载程序集</b>。
///
/// <para>为什么不用 <c>Assembly.LoadFrom</c>：这些 dll 引用的是 IL2CPP 的 interop 程序集，
/// 加载它们会把 BepInEx/游戏类型拖进来，轻则失败重则崩；而且我们要的只是几个自定义特性，
/// 读元数据就够了。</para>
///
/// <para>读出来的东西直接决定了「能不能安全禁用」：
/// <list type="bullet">
///   <item><c>[BepInPlugin]</c> → GUID，依赖关系的键；</item>
///   <item><c>[BepInDependency(guid, HardDependency)]</c> → 少了它插件会被链式加载器直接丢弃；</item>
///   <item><c>[BepInIncompatibility]</c> → 声明式互斥；</item>
///   <item><c>[BepInProcess]</c> → 只在指定进程名里加载（写错了插件根本不生效）。</item>
/// </list></para>
/// </summary>
internal static class ModMetadata
{
    /// <summary>把一个已安装模组的信息补全（就地修改）。读失败时静默跳过（dll 可能不是托管程序集）。</summary>
    public static void ReadInto(InstalledMod mod)
    {
        string? dll = ResolvePrimaryDll(mod);
        if (dll == null) return;

        DependencyFlags.Ensure();

        try
        {
            using var fs = File.OpenRead(dll);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return;

            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.CustomAttributes)
            {
                var ca = reader.GetCustomAttribute(handle);
                string? typeName = AttributeTypeName(reader, ca);
                if (typeName == null) continue;

                switch (typeName)
                {
                    case "BepInPlugin":
                        ReadBepInPlugin(reader, ca, mod);
                        break;
                    case "BepInDependency":
                        ReadBepInDependency(reader, ca, mod);
                        break;
                    case "BepInIncompatibility":
                        ReadBepInIncompatibility(reader, ca, mod);
                        break;
                    case "BepInProcess":
                        ReadBepInProcess(reader, ca, mod);
                        break;
                }
            }
        }
        catch
        {
            // 非托管 dll / 元数据损坏：保持字段为空即可，不影响其它模组。
        }
    }

    /// <summary>取模组的主 dll：单文件就是它自己，文件夹式取里面最大的那个 dll。</summary>
    private static string? ResolvePrimaryDll(InstalledMod mod)
    {
        if (!mod.IsFolder) return File.Exists(mod.FilePath) ? mod.FilePath : null;

        try
        {
            return Directory.EnumerateFiles(mod.FilePath, "*.dll", SearchOption.AllDirectories)
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    // ------------------------------------------------------------ 特性解析

    private static void ReadBepInPlugin(MetadataReader reader, CustomAttribute ca, InstalledMod mod)
    {
        var args = Decode(reader, ca);
        if (args.Length < 3) return;

        mod.Guid = args[0] as string;
        mod.PluginName = args[1] as string;
        mod.KnownVersion ??= args[2] as string;
    }

    private static void ReadBepInDependency(MetadataReader reader, CustomAttribute ca, InstalledMod mod)
    {
        var args = Decode(reader, ca);
        if (args.Length == 0 || args[0] is not string guid || guid.Length == 0) return;

        // 第二个参数是 DependencyFlags。**值不能猜** —— BepInEx 的枚举是
        // [Flags] HardDependency = 1, SoftDependency = 2，不是从 0 开始的。
        // 而且 [BepInDependency(guid)] 这种单参数写法，编译器会把可选参数的默认值
        // 也编进 blob，所以这里永远能读到 flags。真值从 BepInEx 程序集里读（见 EnsureDependencyFlags）。
        long flags = args.Length > 1 && args[1] is int f ? f
                   : args.Length > 1 && args[1] is long l ? l
                   : DependencyFlags.Hard;

        if (flags == DependencyFlags.Soft) mod.SoftDependencies.Add(guid);
        else mod.HardDependencies.Add(guid);
    }

    /// <summary>
    /// <c>DependencyFlags</c> 的实际取值：从 <c>BepInEx.Core.dll</c> 的元数据里读枚举常量，
    /// 保证和**这套安装里真正在用的那个 BepInEx**一致。读不到时退回 BepInEx 5/6 的实际值。
    /// </summary>
    private static class DependencyFlags
    {
        public static long Hard { get; private set; } = 1;
        public static long Soft { get; private set; } = 2;

        private static bool _resolved;

        public static void Ensure()
        {
            if (_resolved) return;
            _resolved = true;

            try
            {
                if (AppState.BepInExDir is not { } bep) return;
                string core = Path.Combine(bep, "core", "BepInEx.Core.dll");
                if (!File.Exists(core)) return;

                var constants = ReadEnumConstants(core, "DependencyFlags");
                if (constants.TryGetValue("HardDependency", out long hard)) Hard = hard;
                if (constants.TryGetValue("SoftDependency", out long soft)) Soft = soft;
            }
            catch { /* 读不到就用默认值 */ }
        }
    }

    /// <summary>
    /// 读一个枚举类型的字面量常量（名字 → 值）。用于把"按枚举名判断"变成
    /// "按这套 BepInEx 的真实定义判断"。
    /// </summary>
    public static Dictionary<string, long> ReadEnumConstants(string assemblyPath, string enumTypeName)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);

        using var fs = File.OpenRead(assemblyPath);
        using var pe = new PEReader(fs);
        if (!pe.HasMetadata) return result;

        var reader = pe.GetMetadataReader();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (reader.GetString(type.Name) != enumTypeName) continue;

            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                if ((field.Attributes & FieldAttributes.Literal) == 0) continue;

                var constantHandle = field.GetDefaultValue();
                if (constantHandle.IsNil) continue;

                var constant = reader.GetConstant(constantHandle);
                if (constant.Value.IsNil) continue;

                result[reader.GetString(field.Name)] = ReadConstantInt(reader, constant);
            }
            break;
        }

        return result;
    }

    private static long ReadConstantInt(MetadataReader reader, Constant constant)
    {
        var bytes = reader.GetBlobBytes(constant.Value);
        return constant.TypeCode switch
        {
            ConstantTypeCode.SByte or ConstantTypeCode.Byte => bytes.Length > 0 ? bytes[0] : 0,
            ConstantTypeCode.Int16 or ConstantTypeCode.UInt16 => bytes.Length >= 2 ? BitConverter.ToInt16(bytes, 0) : 0,
            ConstantTypeCode.Int32 or ConstantTypeCode.UInt32 => bytes.Length >= 4 ? BitConverter.ToInt32(bytes, 0) : 0,
            ConstantTypeCode.Int64 or ConstantTypeCode.UInt64 => bytes.Length >= 8 ? BitConverter.ToInt64(bytes, 0) : 0,
            _ => 0,
        };
    }

    private static void ReadBepInIncompatibility(MetadataReader reader, CustomAttribute ca, InstalledMod mod)
    {
        var args = Decode(reader, ca);
        if (args.Length > 0 && args[0] is string guid && guid.Length > 0)
            mod.Incompatibilities.Add(guid);
    }

    private static void ReadBepInProcess(MetadataReader reader, CustomAttribute ca, InstalledMod mod)
    {
        var args = Decode(reader, ca);
        if (args.Length > 0 && args[0] is string name && name.Length > 0)
            mod.ProcessFilter = name;
    }

    private static object?[] Decode(MetadataReader reader, CustomAttribute ca)
    {
        try
        {
            var value = ca.DecodeValue(AttributeTypeProvider.Instance);
            return value.FixedArguments.Select(a => a.Value).ToArray();
        }
        catch { return Array.Empty<object?>(); }
    }

    /// <summary>取自定义特性的类型名（去掉命名空间与 Attribute 后缀）。</summary>
    private static string? AttributeTypeName(MetadataReader reader, CustomAttribute ca)
    {
        try
        {
            string? full = ca.Constructor.Kind switch
            {
                HandleKind.MethodDefinition =>
                    TypeDefName(reader, reader.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).GetDeclaringType()),
                HandleKind.MemberReference =>
                    reader.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent.Kind == HandleKind.TypeReference
                        ? TypeRefName(reader, (TypeReferenceHandle)reader.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent)
                        : null,
                _ => null,
            };

            if (full == null) return null;

            int dot = full.LastIndexOf('.');
            string name = dot >= 0 ? full[(dot + 1)..] : full;
            const string suffix = "Attribute";
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
                name = name[..^suffix.Length];
            return name;
        }
        catch { return null; }
    }

    private static string TypeDefName(MetadataReader reader, TypeDefinitionHandle handle) =>
        reader.GetString(reader.GetTypeDefinition(handle).Name);

    private static string TypeRefName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var tr = reader.GetTypeReference(handle);
        string ns = tr.Namespace.IsNil ? "" : reader.GetString(tr.Namespace);
        string name = reader.GetString(tr.Name);
        return ns.Length > 0 ? ns + "." + name : name;
    }

    /// <summary>
    /// 给 <see cref="CustomAttribute.DecodeValue{TTypeProvider,T}"/> 用的最小类型提供器：
    /// 我们只关心 string / int / enum(底层 int)，其它一律当 object 处理。
    /// </summary>
    private sealed class AttributeTypeProvider : ICustomAttributeTypeProvider<object?>
    {
        public static readonly AttributeTypeProvider Instance = new();

        public object? GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.String => typeof(string),
            PrimitiveTypeCode.Int32 => typeof(int),
            PrimitiveTypeCode.Boolean => typeof(bool),
            _ => typeof(object),
        };

        public object? GetSystemType() => typeof(Type);
        public object? GetSZArrayType(object? elementType) => typeof(object[]);

        public object? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => typeof(object);
        public object? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => typeof(object);
        public object? GetTypeFromSerializedName(string name) => typeof(object);

        /// <summary>枚举一律按 Int32 读（BepInEx 的 <c>DependencyFlags</c> 就是 int）。</summary>
        public PrimitiveTypeCode GetUnderlyingEnumType(object? type) => PrimitiveTypeCode.Int32;

        public bool IsSystemType(object? type) => Equals(type, typeof(Type));
    }
}
