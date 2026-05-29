using HiveDB;

//if (args.Length == 0)
//{
//    Console.Error.WriteLine("用法: FolderScanner <文件夹路径> [输出数据库路径] [密码]");
//    Console.Error.WriteLine("示例: FolderScanner C:\\MyProject scan.db mypassword");
//    return 1;
//}

string scanPath = Path.GetFullPath("C:\\src\\Project\\Valency.Database");
string dbPath = args.Length > 1 ? args[1] : "folderscan.db";
string? password = args.Length > 2 ? args[2] : "123456";

if (!Directory.Exists(scanPath))
{
    Console.Error.WriteLine($"文件夹不存在: {scanPath}");
    return 1;
}

if (File.Exists(dbPath)) File.Delete(dbPath);

Console.WriteLine($"扫描: {scanPath}");
Console.WriteLine($"输出: {dbPath}");
if (password != null)
    Console.WriteLine($"加密: 已启用");

Console.WriteLine(new string('-', 60));

using var db = RegistryDatabase.Create(dbPath, password);

var sw = System.Diagnostics.Stopwatch.StartNew();
int fileCount = 0;
int dirCount = 0;

Scan(scanPath, "");
sw.Stop();

Console.WriteLine($"文件夹: {dirCount}");
Console.WriteLine($"文件:   {fileCount}");
Console.WriteLine($"耗时:   {sw.ElapsedMilliseconds} ms");
Console.WriteLine($"大小:   {db.FileSize / 1024.0:F1} KB");
return 0;

// ── Recursive scan ─────────────────────────────────────────

void Scan(string fullPath, string relativePath)
{
    foreach (string dir in Directory.GetDirectories(fullPath))
    {
        var dirInfo = new DirectoryInfo(dir);
        string keyName = MakeKey("Dir", relativePath, dirInfo.Name);

        db.CreateKey(keyName);
        WriteTime(keyName, "Created", dirInfo.CreationTimeUtc);
        WriteTime(keyName, "Modified", dirInfo.LastWriteTimeUtc);
        WriteAttr(keyName, dirInfo.Attributes);

        dirCount++;
        Scan(dir, JoinPath(relativePath, dirInfo.Name));
    }

    foreach (string file in Directory.GetFiles(fullPath))
    {
        var fileInfo = new FileInfo(file);
        string keyName = MakeKey("File", relativePath, fileInfo.Name);

        db.CreateKey(keyName);
        db.SetValue(keyName, "Size", fileInfo.Length, RegistryValueKind.QWord);
        WriteTime(keyName, "Created", fileInfo.CreationTimeUtc);
        WriteTime(keyName, "Modified", fileInfo.LastWriteTimeUtc);
        WriteAttr(keyName, fileInfo.Attributes);

        fileCount++;
    }
}

void WriteTime(string key, string name, DateTime utc)
{
    db.SetValue(key, name, utc.ToString("o"), RegistryValueKind.String);
}

void WriteAttr(string key, FileAttributes attr)
{
    db.SetValue(key, "Attributes", (int)attr, RegistryValueKind.DWord);
}

string MakeKey(string prefix, string parent, string name) =>
    parent == "" ? $"{prefix}\\{name}" : $"{prefix}\\{parent}\\{name}";

string JoinPath(string parent, string name) =>
    parent == "" ? name : $"{parent}\\{name}";
