# HiveDB

轻量级嵌入式键值数据库，单文件存储，灵感来源于 Windows 注册表蜂巢（Registry Hive）文件格式。

## 概览

HiveDB 是一个纯 C# 编写、面向 .NET 8 的嵌入式数据库。它将层次化的键值数据存储为单个 `.db` 文件，支持字符串、整数、二进制、多字符串等多种值类型，并提供完整的 CLI 命令行工具。

### 适用场景

- 桌面应用的配置存储
- 嵌入式设备的持久化数据
- 需要层次化键路径（类似注册表）的场景
- 不想引入 SQLite 或外部依赖的小型项目

### 核心特性

- **单文件存储** — 所有数据存放在一个 `.db` 文件中
- **层次化键树** — 键以 `Software\MyApp\Settings` 形式组织，支持父子层级
- **多种值类型** — String、DWord（32位整数）、QWord（64位整数）、Binary、MultiString
- **CRC 完整性校验** — 文件头 CRC-32，每个数据页 CRC-16，保证数据完整性
- **并发安全** — 基于 `ReaderWriterLockSlim`，支持多读单写
- **LRU 页面缓存** — 64 页（256 KB）缓存，加速频繁访问
- **空闲页回收** — 删除数据后页面自动回收复用
- **大值溢出链** — 超大值通过溢出页链表存储，不阻塞键页
- **零外部依赖** — 核心库仅依赖 .NET 8 BCL

---

## 文件格式

### 文件头（第 0 页，4096 字节）

| 偏移 | 大小 | 字段 | 说明 |
|------|------|------|------|
| `0x00` | 4 | Magic | `48 49 56 45` = `HIVE` |
| `0x04` | 4 | Version | 版本号，当前为 `1` |
| `0x08` | 4 | PageSize | 页面大小，固定 `4096` |
| `0x0C` | 4 | FreePageHead | 空闲页链表头部页号 |
| `0x10` | 4 | RootKeyPage | 根键页号（始终为 `1`） |
| `0x14` | 4 | TotalPageCount | 文件总页数 |
| `0x18` | 4 | CRC32 | 前 24 字节的 CRC-32 校验 |

### 键页（Key Page）

每个键页存储一个树节点，包含键名、父子兄弟指针以及值列表。

| 字段 | 大小 | 说明 |
|------|------|------|
| PageType | 1 | `0x02` |
| Flags | 1 | bit 0 = IsDeleted |
| CRC16 | 2 | 第 4 字节到页尾的 CRC-16 |
| KeyNameLen | 2 | 键名 UTF-8 长度 |
| ParentPage | 4 | 父键页号 |
| FirstChildPage | 4 | 第一个子键页号（链表头） |
| NextSiblingPage | 4 | 下一个兄弟页号（链表） |
| ValueCount | 2 | 值条目数量 |
| KeyName | 变长 | 键名（UTF-8） |
| ValueEntries | 变长 | 值条目序列 |

每个值条目：

| 字段 | 大小 | 说明 |
|------|------|------|
| NameLen | 2 | 值名 UTF-8 长度 |
| Kind | 2 | 值类型枚举 |
| DataLen | 4 | 数据长度 |
| OverflowPage | 4 | 溢出页号（0 = 内联存储） |
| Name | 变长 | 值名（UTF-8） |
| InlineData | 变长 | 内联数据（OverflowPage = 0 时） |

### 页面类型

| 值 | 名称 | 用途 |
|----|------|------|
| `0x00` | Free | 空闲页 |
| `0x01` | Header | 文件头 |
| `0x02` | Key | 键页 |
| `0x03` | Overflow | 溢出数据页 |

---

## API 参考

### 创建与打开

```csharp
// 创建新数据库
using var db = RegistryDatabase.Create("config.db");

// 打开已有数据库（只读）
using var db = RegistryDatabase.Open("config.db", readOnly: true);

// 打开已有数据库（读写）
using var db = RegistryDatabase.Open("config.db");
```

### 键操作

```csharp
// 创建键（自动创建中间路径）
db.CreateKey(@"Software\MyApp\Settings");

// 检查键是否存在
bool exists = db.KeyExists(@"Software\MyApp\Settings");

// 获取子键列表
string[] subKeys = db.GetSubKeyNames(@"Software\MyApp");

// 删除键（递归删除子键）
db.DeleteKey(@"Software\MyApp", recursive: true);
```

### 值操作

```csharp
// 写入值（显式指定类型）
db.SetValue(@"Software\MyApp\Settings", "Theme", "dark", RegistryValueKind.String);
db.SetValue(@"Software\MyApp\Settings", "MaxItems", 100, RegistryValueKind.DWord);
db.SetValue(@"Software\MyApp\Settings", "MaxSize", 1024L * 1024 * 1024, RegistryValueKind.QWord);
db.SetValue(@"Software\MyApp\Settings", "Thumbnail", new byte[] { 0xFF, 0xD8 }, RegistryValueKind.Binary);
db.SetValue(@"Software\MyApp\Settings", "Hosts", new[] { "localhost", "127.0.0.1" }, RegistryValueKind.MultiString);

// 读取值
object? theme = db.GetValue(@"Software\MyApp\Settings", "Theme");  // "dark"
object? count = db.GetValue(@"Software\MyApp\Settings", "MaxItems"); // 100

// 获取值类型
RegistryValueKind kind = db.GetValueKind(@"Software\MyApp\Settings", "MaxSize"); // QWord

// 获取值名列表
string[] names = db.GetValueNames(@"Software\MyApp\Settings");

// 删除值
db.DeleteValue(@"Software\MyApp\Settings", "Thumbnail");
```

### 使用 RegistryKey 对象模型

```csharp
using var key = db.RootKey.CreateSubKey(@"Software\MyApp");

// 隐式类型推断
key.SetValue("Theme", "dark");         // → String
key.SetValue("Count", 42);             // → DWord
key.SetValue("MaxSize", 100L * 1024);  // → QWord

// 泛型读取
string? theme = key.GetValue<string>("Theme");
int? count = key.GetValue<int>("Count");

// 遍历
foreach (var subKeyName in key.GetSubKeyNames())
    Console.WriteLine(subKeyName);
```

### 支持的值类型

| 枚举值 | C# 类型 | 说明 |
|--------|---------|------|
| `String` | `string` | UTF-8 字符串 |
| `DWord` | `int` | 32 位有符号整数 |
| `QWord` | `long` | 64 位有符号整数 |
| `Binary` | `byte[]` | 原始字节数组 |
| `MultiString` | `string[]` | 字符串数组 |
| `ExpandString` | `string` | 扩展字符串（同 String） |
| `None` | `byte[]` | 无类型提示的二进制 |

---

## 命令行工具

```
用法:
  HiveDB.CLI [command] [options]

命令:
  create      <file>                          创建新的 HiveDB 数据库文件
  set         <file> <path> <name> <value>    在数据库中设置值
  get         <file> <path> <name>            从数据库中获取值
  enum        <file> <path>                   列出指定路径下的子键和值
  delete-key  <file> <path>                   删除键（别名: rmkey）
  delete-value <file> <path> <name>           删除值（别名: rmval）
  info        <file>                          显示数据库元数据
  test        <file>                          运行功能测试
  bench       <file>                          运行性能基准测试
```

### 使用示例

```bash
# 创建数据库
HiveDB.CLI create myapp.db

# 写入配置
HiveDB.CLI set myapp.db "App\\UI" Theme dark
HiveDB.CLI set myapp.db "App\\UI" FontSize 14 --kind int
HiveDB.CLI set myapp.db "App\\Network" Timeout 30000 --kind long

# 读取配置
HiveDB.CLI get myapp.db "App\\UI" Theme        # → dark

# 浏览数据
HiveDB.CLI enum myapp.db "App\\UI"
# 输出:
# Key: App\UI
# ------------------------------------------------------------
# Values:
#   FontSize = 14  (DWord)
#   Theme = dark  (String)

# 查看数据库信息
HiveDB.CLI info myapp.db
# 输出:
# 文件:        myapp.db
# 大小:        136.0 KB
# 页数:        34
# 只读:        True
# 键数:        2
# 值数:        2

# 运行功能测试
HiveDB.CLI test test.db

# 性能基准测试
HiveDB.CLI bench bench.db -n 1000
```

---

## 项目结构

```
src/
├── HiveDB/                          # 核心类库
│   ├── HiveDB.csproj
│   ├── HiveDBException.cs           # 自定义异常
│   ├── RegistryDatabase.cs          # 公开 API（工厂方法 + 路径操作）
│   ├── RegistryKey.cs               # 键节点 API
│   ├── RegistryKeyExtensions.cs     # GetValue<T> 泛型扩展
│   ├── RegistryValueKind.cs         # 值类型枚举
│   ├── Storage/
│   │   ├── BinaryFileHandle.cs      # 底层文件 I/O
│   │   ├── Crc16.cs                 # CRC-16 校验
│   │   ├── Crc32.cs                 # CRC-32 校验
│   │   ├── FileHeader.cs            # 文件头结构
│   │   ├── PageCache.cs             # LRU 页面缓存
│   │   ├── PageManager.cs           # 页面分配/回收/溢出链
│   │   └── PageType.cs             # 页面类型枚举
│   ├── Tree/
│   │   ├── KeyPage.cs               # 键页序列化与值条目管理
│   │   └── TreeNavigator.cs         # 路径解析与树遍历
│   └── Value/
│       └── ValueSerializer.cs       # 所有值类型的序列化/反序列化
├── HiveDB.Cli/                      # 命令行工具
│   ├── HiveDB.Cli.csproj
│   └── Program.cs                   # 9 个命令的完整实现
└── README.md
```

## 构建

```bash
dotnet build src/HiveDB.Cli/HiveDB.Cli.csproj
```

## 许可

本项目仅用于学习和研究目的。
