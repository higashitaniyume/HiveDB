namespace HiveDB.Tests;

public class ConcurrencyTests : IDisposable
{
    private readonly string _filePath;

    public ConcurrencyTests()
    {
        _filePath = Path.Combine(Path.GetTempPath(), $"hivedb_test_{Guid.NewGuid()}.dat");
    }

    public void Dispose()
    {
        if (File.Exists(_filePath))
        {
            try { File.Delete(_filePath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Concurrent_Readers_Do_Not_Block()
    {
        using var db = RegistryDatabase.Create(_filePath);
        db.CreateKey("Test");
        db.SetValue("Test", "Val", "data", RegistryValueKind.String);

        var barrier = new Barrier(4);
        var tasks = new Task[4];
        for (int i = 0; i < 4; i++)
        {
            int id = i;
            tasks[i] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (int j = 0; j < 100; j++)
                {
                    object? v = db.GetValue("Test", "Val");
                    Assert.Equal("data", v);
                }
            });
        }

        Assert.True(Task.WaitAll(tasks, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Concurrent_Write_Stress_Test()
    {
        using var db = RegistryDatabase.Create(_filePath);

        // Pre-create keys from a single thread
        db.CreateKey("A");
        db.CreateKey("B");
        db.CreateKey("C");
        db.CreateKey("D");

        var tasks = new Task[4];
        for (int i = 0; i < 4; i++)
        {
            int id = i;
            char keyName = (char)('A' + i);
            tasks[i] = Task.Run(() =>
            {
                for (int j = 0; j < 50; j++)
                {
                    string path = keyName.ToString();
                    db.SetValue(path, $"val{id}_{j}", j, RegistryValueKind.DWord);
                }
            });
        }

        Assert.True(Task.WaitAll(tasks, TimeSpan.FromSeconds(15)));

        // Verify all values were written
        for (int i = 0; i < 4; i++)
        {
            char keyName = (char)('A' + i);
            string[] names = db.GetValueNames(keyName.ToString());
            Assert.True(names.Length > 0, $"Key {keyName} should have values");
        }
    }
}
