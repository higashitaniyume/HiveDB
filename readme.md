# HiveDB

基于二进制文件的树形数据库，API 设计参考 Windows 注册表（`Microsoft.Win32.Registry`），支持层级键值存储、多种数据类型、路径直读。

## 项目结构

```
src/HiveDB/         # 类库 (.NET 9.0)
test/HiveDB.Tests/  # xUnit 测试项目 (88 条测试)
```

## 快速开始

```csharp
using HiveDB;

// 创建数据库文件
using var db = RegistryDatabase.Create(@"C:\data\mydb.dat");

// ---- 方式一：路径直读 ----
db.CreateKey(@"Software\MyApp\Settings");
db.SetValue(@"Software\MyApp\Settings", "Theme", "dark", RegistryValueKind.String);
db.SetValue(@"Software\MyApp\Settings", "Count", 42, RegistryValueKind.DWord);
db.SetValue(@"Software\MyApp\Settings", "Data", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);

object? theme = db.GetValue(@"Software\MyApp\Settings", "Theme"); // "dark"
bool exists = db.KeyExists(@"Software\MyApp\Settings");           // true
db.DeleteKey(@"Software\MyApp", recursive: true);

// ---- 方式二：RegistryKey 导航 ----
using var root = db.RootKey;
using var key = root.CreateSubKey(@"Software\MyApp");
key.SetValue("Version", "1.0.0");           // 自动推断类型为 String
key.SetValue("MaxUsers", 100);              // 自动推断类型为 DWord
key.SetValue("Config", new string[] { "a", "b" }); // 自动推断为 MultiString

string? ver = key.GetValue<string>("Version");  // 泛型取值

// 只读打开
using var ro = RegistryDatabase.Open(@"C:\data\mydb.dat", readOnly: true);
```

## 支持的数据类型

| 枚举值 | 对应 Registry | C# 类型 |
|--------|-------------|---------|
| `String` | REG_SZ | `string` |
| `ExpandString` | REG_EXPAND_SZ | `string` |
| `Binary` | REG_BINARY | `byte[]` |
| `DWord` | REG_DWORD | `int` |
| `QWord` | REG_QWORD | `long` |
| `MultiString` | REG_MULTI_SZ | `string[]` |
| `None` | REG_NONE | `byte[]` |

## API 概览

### RegistryDatabase

| 方法 | 说明 |
|------|------|
| `Create(path)` | 创建新数据库文件 |
| `Open(path, readOnly?)` | 打开已有数据库 |
| `RootKey` | 获取根键（RegistryKey） |
| `CreateKey(path)` | 创建键（含所有中间层） |
| `DeleteKey(path, recursive?)` | 删除键 |
| `KeyExists(path)` | 判断键是否存在 |
| `GetSubKeyNames(path)` | 获取所有子键名称 |
| `SetValue(path, name, value, kind)` | 设置值 |
| `GetValue(path, name, default?)` | 获取值 |
| `GetValueKind(path, name)` | 获取值的类型 |
| `DeleteValue(path, name)` | 删除值 |
| `GetValueNames(path)` | 获取所有值名称 |
| `Flush()` | 强制刷盘 |
| `Dispose()` | 关闭文件 |

### RegistryKey

| 方法 | 说明 |
|------|------|
| `CreateSubKey(name)` | 创建子键（支持路径如 `"A\B\C"`） |
| `OpenSubKey(name, writable?)` | 打开子键 |
| `DeleteSubKey(name, recursive?)` | 删除子键 |
| `HasSubKey(name)` | 判断子键是否存在 |
| `GetSubKeyNames()` | 获取所有子键名称 |
| `SetValue(name, value)` | 设置值（自动推断类型） |
| `SetValue(name, value, kind)` | 设置值（显式指定类型） |
| `GetValue(name, default?)` | 获取值 |
| `GetValueKind(name)` | 获取值类型 |
| `DeleteValue(name)` | 删除值 |
| `GetValueNames()` | 获取所有值名称 |
| `Name` / `FullPath` | 键名称 / 完整路径 |
| `ParentKey` | 父键 |
| `Database` | 所属数据库 |
| `Dispose()` | 释放句柄 |

### RegistryKeyExtensions

| 方法 | 说明 |
|------|------|
| `GetValue<T>(name, default?)` | 泛型取值 |

## 文件格式

- **页大小**：4096 字节
- **页 0**：文件头（魔数 `VREG`、版本号、根键位置、空闲链表头、CRC32）
- **Key 页**：键名、父子/兄弟页指针、内联值列表（带 CRC16）
- **Overflow 页**：大值（>~4KB）的链表存储
- **Free 页**：已删除页的空闲链表，分配时优先复用

## 限制

| 项目 | 限制 |
|------|------|
| 键名 | 最多 255 字节 (UTF-8) |
| 路径深度 | 最多 32 层 |
| 单个值 | 最大 16 MB |

## 异常

| 异常 | 触发场景 |
|------|----------|
| `HiveDBException` | 文件损坏、魔数错误、CRC 校验失败 |
| `KeyNotFoundException` | 键或值不存在 |
| `InvalidOperationException` | 只读数据库写入、删除有子键的非递归操作 |
| `ArgumentException` | 非法路径/名称 |
| `ObjectDisposedException` | 已释放对象上操作 |

## 构建与测试

```bash
dotnet build HiveDB.sln
dotnet test HiveDB.sln
```
