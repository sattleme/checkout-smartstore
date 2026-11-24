using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sattleme.Checkout.Internal;

/// <summary>
/// One at a time per key, in this process (SPEC.md §17: the calls of one order, one after the
/// other; §31: the rest invoices of one invoice asked one at a time). A key's lock is dropped once
/// nobody holds or waits for it.
/// </summary>
internal sealed class KeyedLock
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed class Entry : IDisposable
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;

        public void Dispose() => Semaphore.Dispose();
    }

    /// <summary>Holds <paramref name="key"/> until the result is disposed; waits for the one who holds it.</summary>
    public async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }
            entry.Users++;
        }
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Leave(key, entry);
            throw;
        }
        return new Holder(this, key, entry);
    }

    private void Leave(string key, Entry entry)
    {
        lock (_gate)
        {
            entry.Users--;
            if (entry.Users == 0)
            {
                _entries.Remove(key);
                entry.Dispose();
            }
        }
    }

    private sealed class Holder(KeyedLock owner, string key, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }
            entry.Semaphore.Release();
            owner.Leave(key, entry);
        }
    }
}
