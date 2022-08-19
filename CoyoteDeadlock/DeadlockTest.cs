using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using System.Threading;
using Xunit.Abstractions;
using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;
using System.Linq;
using System.Runtime.CompilerServices;
using System.IO;

namespace CoyoteDeadlock;

public class Item
{
    public string Name { get; set; }
}

public interface ICache
{
    void Set(string key, object value);
    object Get(string key);
}

public interface ILock
{
    Task WaitAsync();
    void Release();
}

public interface IItemsRepository
{
    Task<IEnumerable<Item>> GetItemsAsync();
}

public interface IItemsService
{
    Task<IEnumerable<Item>> GetItemsAsync();
}

public class DumbCache: ICache
{
    private readonly Dictionary<string, object> _cache = new Dictionary<string, object>();

    public object Get(string key)
    {
        return _cache.TryGetValue(key, out object value) ? value : null;
    }

    public void Set(string key, object value)
    {
        _cache[key] = value;
    }
}

public class DumbRepo : IItemsRepository
{
    public async Task<IEnumerable<Item>> GetItemsAsync()
    {
        return await Task.Run(() => new[]
        {
            new Item { Name = "Item1"},
            new Item { Name = "Item2"}
        });
    }
}

public class DumbLock : ILock
{
    public int i = 0;

    public void Release()
    {
        i -= 1;
    }

    public async Task WaitAsync()
    {
        i += 1;

        while (true)
        {
            if (i == 1) return;
            await Task.Delay(5);
        }
    }
}
public class SemaphoreSlimLock : ILock
{
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

    public void Release()
    {
        _semaphore.Release();
    }

    public Task WaitAsync()
    {
        return _semaphore.WaitAsync();
    }
}

public class ItemsService : IItemsService
{
    private const string CacheKey = "items";

    private readonly IItemsRepository _itemsRepository;
    private readonly ICache _cache;
    private readonly ILock _lock;

    public ItemsService(IItemsRepository itemsRepository, ICache cache, ILock lck)
    {
        _itemsRepository = itemsRepository ?? throw new ArgumentNullException(nameof(itemsRepository));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _lock = lck ?? throw new ArgumentNullException(nameof(lck));
    }

    public async Task<IEnumerable<Item>> GetItemsAsync()
    {
        var items = _cache.Get(CacheKey);

        if (items != null)
        {
            return (IEnumerable<Item>)items;
        }

        await _lock.WaitAsync();
        try
        {
            items = _cache.Get(CacheKey);

            if (items != null)
            {
                return (IEnumerable<Item>)items;
            }

            items = await _itemsRepository.GetItemsAsync();
            _cache.Set(CacheKey, items);

            return (IEnumerable<Item>)items;
        }

        finally
        {
            _lock.Release();
        }        
    }
}

public class DeadlockTest
{
    private readonly ITestOutputHelper _output;

    public DeadlockTest(ITestOutputHelper output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }
   
    public static async Task TestMethod(IItemsRepository repo, ICache cache, ILock lck)
    {
        var service = new ItemsService(repo, cache, lck);

        var task1 = service.GetItemsAsync();
        var task2 = service.GetItemsAsync();

        var restults = await Task.WhenAll(task1, task2);

        // here assertions like checking is repo received only one call
    }

    [Fact]
    public static void TestWithSemaphoreSlimLock()
    {
        RunSystematicTest(() => TestMethod(new DumbRepo(), new DumbCache(), new SemaphoreSlimLock()));
    }

    [Fact]
    public static void TestWithDumbLock()
    {
        RunSystematicTest(() => TestMethod(new DumbRepo(), new DumbCache(), new DumbLock()));
    }

    private static void RunSystematicTest(Func<Task> test, [CallerMemberName] string testName = null)
    {
        var configuration = Configuration.Create()
            .WithTestingIterations(100)
            .WithVerbosityEnabled();

        var testingEngine = TestingEngine.Create(configuration, test);
        testingEngine.Run();

        if (testingEngine.TestReport.NumOfFoundBugs > 0)
        {
            var error = testingEngine.TestReport.BugReports.First();
            File.WriteAllText(testName + ".schedule", testingEngine.ReproducibleTrace);
            Assert.True(false, $"Found bug: {error}");
        }
    }
}
