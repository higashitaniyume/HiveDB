using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Globalization;
using HiveDB;

// 设置中文本地化
Thread.CurrentThread.CurrentUICulture = new CultureInfo("zh-Hans");

// ── 共享选项 / 参数 ──────────────────────────────────────────
var fileArg = new Argument<FileInfo>("file") { Description = "HiveDB 文件路径" };
ArgumentValidation.AcceptExistingOnly(fileArg);

var fileArgNew = new Argument<FileInfo>("file") { Description = "要创建的新 HiveDB 文件路径" };

var pathArg = new Argument<string>("path") { Description = "注册表式键路径，例如 Software\\MyApp\\Settings" };

var nameArg = new Argument<string>("name") { Description = "值名称" };

var valueArg = new Argument<string>("value") { Description = "要存储的值" };

var kindOpt = new Option<string>("--kind", "-k") { Description = "值类型: auto|string|dword|qword|binary|multi|hex" };
kindOpt.DefaultValueFactory = _ => "auto";

var recursiveOpt = new Option<bool>("--recursive", "-r") { Description = "删除键及其所有子键" };
recursiveOpt.DefaultValueFactory = _ => false;

var countOpt = new Option<int>("--count", "-n") { Description = "迭代次数" };
countOpt.DefaultValueFactory = _ => 100;

var pathArgOpt = new Argument<string>("path") { Description = "注册表式键路径（默认: 根路径）" };
pathArgOpt.DefaultValueFactory = _ => "";

var passwordOpt = new Option<string?>("--password", "-p") { Description = "数据库密码（用于加密/签名）" };
passwordOpt.DefaultValueFactory = _ => null;

var signOpt = new Option<bool>("--sign", "-s") { Description = "仅签名模式（不加密，仅验证完整性）" };
signOpt.DefaultValueFactory = _ => false;

// ── 根命令 ──────────────────────────────────────────────────
var root = new RootCommand("HiveDB CLI —— HiveDB 数据库文件交互工具")
{
    CreateCommand(fileArgNew, passwordOpt, signOpt),
    SetCommand(fileArg, pathArg, nameArg, valueArg, kindOpt, passwordOpt),
    GetCommand(fileArg, pathArg, nameArg, passwordOpt),
    EnumCommand(fileArg, pathArgOpt, passwordOpt),
    DeleteKeyCommand(fileArg, pathArg, recursiveOpt, passwordOpt),
    DeleteValueCommand(fileArg, pathArg, nameArg, passwordOpt),
    InfoCommand(fileArg, passwordOpt),
    TreeCommand(fileArg, pathArgOpt, passwordOpt),
    TestCommand(fileArgNew, passwordOpt, signOpt),
    BenchCommand(fileArgNew, countOpt, passwordOpt, signOpt),
};

// 自定义帮助选项为中文
var helpOpt = root.Options.OfType<HelpOption>().First();
helpOpt.Description = "显示帮助和使用信息";

var parseResult = root.Parse(args);
return await parseResult.InvokeAsync(new InvocationConfiguration(), CancellationToken.None);

// ── 命令构建器 ──────────────────────────────────────────────

static Command CreateCommand(Argument<FileInfo> fileArg, Option<string?> passwordOpt, Option<bool> signOpt)
{
    var cmd = new Command("create", "创建新的 HiveDB 数据库文件");
    cmd.Add(fileArg);
    cmd.Add(passwordOpt);
    cmd.Add(signOpt);
    cmd.SetAction((ParseResult pr) =>
    {
        var file = pr.GetValue(fileArg)!;
        var password = pr.GetValue(passwordOpt);
        var signOnly = pr.GetValue(signOpt);
        using var db = RegistryDatabase.Create(file.FullName, password, signOnly);
        Console.WriteLine($"已创建: {file.FullName}");
        Console.WriteLine($"  页数: {db.FileSize / 4096}，大小: {FormatBytes(db.FileSize)}");
    });
    return cmd;
}

static Command SetCommand(Argument<FileInfo> fileArg, Argument<string> pathArg,
    Argument<string> nameArg, Argument<string> valueArg, Option<string> kindOpt, Option<string?> passwordOpt)
{
    var cmd = new Command("set", "在数据库中设置值");
    cmd.Add(fileArg);
    cmd.Add(pathArg);
    cmd.Add(nameArg);
    cmd.Add(valueArg);
    cmd.Add(kindOpt);
    cmd.Add(passwordOpt);
    cmd.SetAction((ParseResult pr) =>
    {
        var file = pr.GetValue(fileArg)!;
        var keyPath = pr.GetValue(pathArg)!;
        var name = pr.GetValue(nameArg)!;
        var rawValue = pr.GetValue(valueArg)!;
        var kindStr = pr.GetValue(kindOpt)!;
        var password = pr.GetValue(passwordOpt);
        using var db = RegistryDatabase.Open(file.FullName, password: password);
        db.CreateKey(keyPath);
        (RegistryValueKind kind, object value) = ParseValue(rawValue, kindStr);
        db.SetValue(keyPath, name, value, kind);
        Console.WriteLine($"已设置 '{name}' = {FormatValue(value)} ({kind})");
    });
    return cmd;
}

static Command GetCommand(Argument<FileInfo> fileArg, Argument<string> pathArg,
    Argument<string> nameArg, Option<string?> passwordOpt)
{
    var cmd = new Command("get", "从数据库中获取值");
    cmd.Add(fileArg);
    cmd.Add(pathArg);
    cmd.Add(nameArg);
    cmd.Add(passwordOpt);
    cmd.SetAction((ParseResult pr) =>
    {
        var file = pr.GetValue(fileArg)!;
        var keyPath = pr.GetValue(pathArg)!;
        var name = pr.GetValue(nameArg)!;
        var password = pr.GetValue(passwordOpt);
        using var db = RegistryDatabase.Open(file.FullName, readOnly: true, password: password);
        if (!db.KeyExists(keyPath)) { Console.Error.WriteLine($"键不存在: '{keyPath}'"); return; }
        object? val = db.GetValue(keyPath, name);
        if (val is null) { Console.Error.WriteLine($"值不存在: '{name}'"); return; }
        var kind = db.GetValueKind(keyPath, name);
        Console.WriteLine(FormatValue(val));
        Console.Error.WriteLine($"(类型: {kind})");
    });
    return cmd;
}

static Command EnumCommand(Argument<FileInfo> fileArg, Argument<string> pathArg, Option<string?> passwordOpt)
{
    var cmd = new Command("enum", "列出指定路径下的子键和值");
    cmd.Add(fileArg);
    cmd.Add(pathArg);
    cmd.Add(passwordOpt);
    cmd.SetAction((ParseResult pr) =>
    {
        var file = pr.GetValue(fileArg)!;
        var keyPath = pr.GetValue(pathArg)!;
        var password = pr.GetValue(passwordOpt);
        using var db = RegistryDatabase.Open(file.FullName, readOnly: true, password: password);
        if (!db.KeyExists(keyPath)) { Console.Error.WriteLine($"键不存在: '{keyPath}'"); return; }

        Console.WriteLine($"键: {(keyPath == "" ? "(根)" : keyPath)}");
        Console.WriteLine(new string('-', 60));

        string[] subKeys = db.GetSubKeyNames(keyPath);
        if (subKeys.Length > 0)
        {
            Console.WriteLine("子键:");
            foreach (var sk in subKeys.OrderBy(x => x))
                Console.WriteLine($"  [{sk}]");
        }

        string[] valueNames = db.GetValueNames(keyPath);
        if (valueNames.Length > 0)
        {
            Console.WriteLine("\n值:");
            foreach (var vn in valueNames.OrderBy(x => x))
            {
                object? v = db.GetValue(keyPath, vn);
                var k = db.GetValueKind(keyPath, vn);
                Console.WriteLine($"  {vn} = {FormatValue(v)}  ({k})");
            }
        }

        if (subKeys.Length == 0 && valueNames.Length == 0)
            Console.WriteLine("  (空)");
    });
    return cmd;
}

static Command DeleteKeyCommand(Argument<FileInfo> fileArg, Argument<string> pathArg,
    Option<bool> recursiveOpt, Option<string?> passwordOpt)
{
    var cmd = new Command("delete-key", "删除键");
    cmd.Add(fileArg);
    cmd.Add(pathArg);
    cmd.Add(recursiveOpt);
    cmd.Add(passwordOpt);
    cmd.Aliases.Add("rmkey");
    cmd.SetAction((ParseResult pr) =>
    {
        var file = pr.GetValue(fileArg)!;
        var keyPath = pr.GetValue(pathArg)!;
        var recursive = pr.GetValue(recursiveOpt);
        var password = pr.GetValue(passwordOpt);
        using var db = RegistryDatabase.Open(file.FullName, password: password);
        db.DeleteKey(keyPath, recursive);
        Console.WriteLine($"已删除键: '{keyPath}' (递归={recursive})");
    });
    return cmd;
}

static Command DeleteValueCommand(Argument<FileInfo> fileArg, Argument<string> pathArg,
    Argument<string> nameArg, Option<string?> passwordOpt)
{
    var cmd = new Command("delete-value", "删除值");
    cmd.Add(fileArg);
    cmd.Add(pathArg);
    cmd.Add(nameArg);
    cmd.Add(passwordOpt);
    cmd.Aliases.Add("rmval");
    cmd.SetAction((ParseResult pr) =>
    {
        var file = pr.GetValue(fileArg)!;
        var keyPath = pr.GetValue(pathArg)!;
        var name = pr.GetValue(nameArg)!;
        var password = pr.GetValue(passwordOpt);
        using var db = RegistryDatabase.Open(file.FullName, password: password);
        db.DeleteValue(keyPath, name);
        Console.WriteLine($"已删除值: '{name}' 来自 '{keyPath}'");
    });
    return cmd;
}

static Command InfoCommand(Argument<FileInfo> fileArg, Option<string?> passwordOpt)
{
    var cmd = new Command("info", "显示数据库元数据");
    cmd.Add(fileArg);
    cmd.Add(passwordOpt);
    cmd.SetAction((ParseResult pr) =>
    {
        var file = pr.GetValue(fileArg)!;
        var password = pr.GetValue(passwordOpt);
        using var db = RegistryDatabase.Open(file.FullName, readOnly: true, password: password);
        int keyCount = 0, valueCount = 0;
        CountTree(db, "", ref keyCount, ref valueCount);

        Console.WriteLine($"文件:        {file.FullName}");
        Console.WriteLine($"大小:        {FormatBytes(db.FileSize)}");
        Console.WriteLine($"页数:        {db.FileSize / 4096}");
        Console.WriteLine($"只读:        {db.IsReadOnly}");
        Console.WriteLine($"键数:        {keyCount}");
        Console.WriteLine($"值数:        {valueCount}");
        Console.WriteLine($"根子键数:    {db.GetSubKeyNames("").Length}");
    });
    return cmd;
}

static Command TreeCommand(Argument<FileInfo> fileArg, Argument<string> pathArg, Option<string?> passwordOpt)
{
    var cmd = new Command("tree", "树形显示数据库结构");
    cmd.Add(fileArg);
    cmd.Add(pathArg);
    cmd.Add(passwordOpt);
    cmd.SetAction((ParseResult pr) =>
    {
        var file = pr.GetValue(fileArg)!;
        var rootPath = pr.GetValue(pathArg)!;
        var password = pr.GetValue(passwordOpt);
        using var db = RegistryDatabase.Open(file.FullName, readOnly: true, password: password);

        if (rootPath != "" && !db.KeyExists(rootPath))
        {
            Console.Error.WriteLine($"键不存在: '{rootPath}'");
            return;
        }

        string displayName = rootPath == "" ? "(根)" : rootPath;
        Console.WriteLine(displayName);

        PrintTree(db, rootPath, "", true);
    });
    return cmd;
}

static void PrintTree(RegistryDatabase db, string path, string indent, bool isLast)
{
    string[] subKeys = db.GetSubKeyNames(path);
    string[] values = db.GetValueNames(path);

    // Show values first
    foreach (var vn in values.OrderBy(x => x))
    {
        object? v = db.GetValue(path, vn);
        var kind = db.GetValueKind(path, vn);
        Console.WriteLine($"{indent}    {vn} = {FormatValue(v)} ({kind})");
    }

    // Show sub-keys with tree connectors
    for (int i = 0; i < subKeys.Length; i++)
    {
        string sk = subKeys[i];
        bool last = i == subKeys.Length - 1;
        string connector = last ? "└── " : "├── ";
        string childIndent = indent + (last ? "    " : "│   ");

        Console.WriteLine($"{indent}{connector}[{sk}]");

        string childPath = path == "" ? sk : $"{path}\\{sk}";
        PrintTree(db, childPath, childIndent, last);
    }
}

static Command TestCommand(Argument<FileInfo> fileArg, Option<string?> passwordOpt, Option<bool> signOpt)
{
    var cmd = new Command("test", "运行功能测试");
    cmd.Add(fileArg);
    cmd.Add(passwordOpt);
    cmd.Add(signOpt);
    cmd.SetAction((ParseResult pr) =>
    {
        var file = pr.GetValue(fileArg)!;
        var password = pr.GetValue(passwordOpt);
        var signOnly = pr.GetValue(signOpt);
        Console.WriteLine("HiveDB 功能测试");
        Console.WriteLine(new string('=', 60));

        if (File.Exists(file.FullName)) File.Delete(file.FullName);
        using var db = RegistryDatabase.Create(file.FullName, password, signOnly);
        Console.WriteLine($"[1/8] 创建 .............. 通过 (页数: {db.FileSize / 4096})");

        db.CreateKey(@"Software\MyApp\Settings");
        db.CreateKey(@"Software\MyApp\Cache");
        db.CreateKey(@"System\Config");
        Console.WriteLine("[2/8] 创建键 ............ 通过");

        bool ok = db.KeyExists(@"Software\MyApp\Settings")
               && db.KeyExists(@"Software\MyApp\Cache")
               && db.KeyExists(@"System\Config");
        Console.WriteLine($"[3/8] 键存在检查 ........ {(ok ? "通过" : "失败")}");

        db.SetValue(@"Software\MyApp\Settings", "Theme", "dark", RegistryValueKind.String);
        db.SetValue(@"Software\MyApp\Settings", "Count", 42, RegistryValueKind.DWord);
        db.SetValue(@"Software\MyApp\Settings", "Ratio", 3.14.GetHashCode(), RegistryValueKind.DWord);
        db.SetValue(@"Software\MyApp\Cache", "MaxSize", 100L * 1024 * 1024, RegistryValueKind.QWord);
        db.SetValue(@"Software\MyApp\Cache", "Data", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, RegistryValueKind.Binary);
        db.SetValue(@"System\Config", "Hosts", new string[] { "localhost", "127.0.0.1" }, RegistryValueKind.MultiString);
        Console.WriteLine("[4/8] 设置值 ............ 通过");

        var theme = db.GetValue(@"Software\MyApp\Settings", "Theme");
        var count = db.GetValue(@"Software\MyApp\Settings", "Count");
        var bytes = db.GetValue(@"Software\MyApp\Cache", "Data");
        ok = (string?)theme == "dark" && (int)count! == 42
          && ((byte[])bytes!).SequenceEqual(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        Console.WriteLine($"[5/8] 获取值 ............ {(ok ? "通过" : "失败")}");

        db.DeleteValue(@"Software\MyApp\Settings", "Ratio");
        ok = db.GetValue(@"Software\MyApp\Settings", "Ratio") is null;
        Console.WriteLine($"[6/8] 删除值 ............ {(ok ? "通过" : "失败")}");

        db.DeleteKey(@"Software\MyApp", recursive: true);
        ok = !db.KeyExists(@"Software\MyApp") && db.KeyExists(@"System\Config");
        Console.WriteLine($"[7/8] 删除键 ............ {(ok ? "通过" : "失败")}");

        db.Flush();
        ok = db.FileSize > 4096 * 2;
        Console.WriteLine($"[8/8] 刷写 + 大小 ....... {(ok ? "通过" : "失败")} (大小: {FormatBytes(db.FileSize)})");

        Console.WriteLine(new string('=', 60));
        Console.WriteLine("测试完成。");
    });
    return cmd;
}

static Command BenchCommand(Argument<FileInfo> fileArg, Option<int> countOpt, Option<string?> passwordOpt, Option<bool> signOpt)
{
    var cmd = new Command("bench", "运行性能基准测试");
    cmd.Add(fileArg);
    cmd.Add(countOpt);
    cmd.Add(passwordOpt);
    cmd.Add(signOpt);
    cmd.SetAction((ParseResult pr) =>
    {
        var file = pr.GetValue(fileArg)!;
        var count = pr.GetValue(countOpt);
        var password = pr.GetValue(passwordOpt);
        var signOnly = pr.GetValue(signOpt);
        const int valuesPerKey = 150; // 每个键最多存储的值数量

        if (File.Exists(file.FullName)) File.Delete(file.FullName);
        using var db = RegistryDatabase.Create(file.FullName, password, signOnly);

        Console.WriteLine($"基准测试: {count} 次迭代 (每键 {valuesPerKey} 个值)");
        Console.WriteLine(new string('=', 50));

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            string keyPath = $"bench\\group_{i / valuesPerKey}";
            if (i % valuesPerKey == 0) db.CreateKey(keyPath);
            db.SetValue(keyPath, $"key_{i}", i, RegistryValueKind.DWord);
        }
        sw.Stop();
        Console.WriteLine($"写入: {count} 个值，耗时 {sw.ElapsedMilliseconds} ms ({count * 1000.0 / sw.ElapsedMilliseconds:F0} 次/秒)");

        sw.Restart();
        long sum = 0;
        for (int i = 0; i < count; i++)
        {
            string keyPath = $"bench\\group_{i / valuesPerKey}";
            sum += (int)db.GetValue(keyPath, $"key_{i}")!;
        }
        sw.Stop();
        Console.WriteLine($"读取: {count} 个值，耗时 {sw.ElapsedMilliseconds} ms ({count * 1000.0 / sw.ElapsedMilliseconds:F0} 次/秒)");
        Console.WriteLine($"校验和: {sum}");

        sw.Restart();
        for (int i = 0; i < count; i++)
        {
            string keyPath = $"bench\\group_{i / valuesPerKey}";
            db.DeleteValue(keyPath, $"key_{i}");
        }
        sw.Stop();
        Console.WriteLine($"删除: {count} 个值，耗时 {sw.ElapsedMilliseconds} ms ({count * 1000.0 / sw.ElapsedMilliseconds:F0} 次/秒)");

        db.Flush();
        Console.WriteLine($"文件大小: {FormatBytes(db.FileSize)}");
    });
    return cmd;
}

// ── 辅助方法 ─────────────────────────────────────────────────

static (RegistryValueKind, object) ParseValue(string raw, string kind)
{
    if (kind == "auto")
    {
        if (int.TryParse(raw, out int iv)) return (RegistryValueKind.DWord, iv);
        if (long.TryParse(raw, out long lv)) return (RegistryValueKind.QWord, lv);
        if (raw.Contains(',')) return (RegistryValueKind.MultiString, raw.Split(','));
        if (raw.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')
            && raw.Length % 2 == 0 && raw.Length > 2)
            return (RegistryValueKind.Binary, Convert.FromHexString(raw));
        return (RegistryValueKind.String, raw);
    }

    return kind switch
    {
        "string" or "str" => (RegistryValueKind.String, raw),
        "dword" or "int" or "i32" => (RegistryValueKind.DWord, int.Parse(raw)),
        "qword" or "long" or "i64" => (RegistryValueKind.QWord, long.Parse(raw)),
        "binary" or "hex" => (RegistryValueKind.Binary, Convert.FromHexString(raw)),
        "multi" or "multistring" => (RegistryValueKind.MultiString, raw.Split(',')),
        _ => throw new ArgumentException($"未知类型: '{kind}'。可用: string|dword|qword|binary|multi|hex|auto")
    };
}

static string FormatValue(object? val) => val switch
{
    null => "(null)",
    byte[] b => BitConverter.ToString(b).Replace("-", ""),
    string[] arr => "[" + string.Join(", ", arr.Select(s => $"\"{s}\"")) + "]",
    _ => val.ToString() ?? "(null)"
};

static string FormatBytes(long bytes) => bytes switch
{
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
    _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
};

static void CountTree(RegistryDatabase db, string path, ref int keyCount, ref int valueCount)
{
    if (path != "") keyCount++;
    valueCount += db.GetValueNames(path).Length;
    foreach (var sub in db.GetSubKeyNames(path))
        CountTree(db, path == "" ? sub : $"{path}\\{sub}", ref keyCount, ref valueCount);
}
