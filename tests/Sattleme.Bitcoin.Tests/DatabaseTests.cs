using Microsoft.EntityFrameworkCore;
using Sattleme.Bitcoin.Domain;
using Sattleme.Bitcoin.Services;
using Sattleme.Bitcoin.Tests.Support;

namespace Sattleme.Bitcoin.Tests;

/// <summary>The shop's database as the tests make it: the module's tables take its records, and its locks hold.</summary>
[TestFixture]
public sealed class DatabaseTests
{
    [Test]
    public async Task The_module_s_tables_take_its_records()
    {
        using var database = new TestDatabase();
        await using var db = database.NewContext();
        var store = new SattlemeStore(db);
        var record = await store.GetOrMakeOrderAsync(42, Guid.NewGuid(), 1, 7, DateTime.UtcNow);
        Assert.That(await store.TryLockOrderAsync(42, "a", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1)), Is.True);
        Assert.That(await store.TryLockOrderAsync(42, "b", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1)), Is.False);
        Assert.That(await db.SattlemeOrders().CountAsync(), Is.EqualTo(1));
        Assert.That(record.PageToken, Has.Length.EqualTo(64));
    }
}
