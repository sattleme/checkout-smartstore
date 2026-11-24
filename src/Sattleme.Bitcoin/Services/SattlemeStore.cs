using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Sattleme.Bitcoin.Domain;
using Smartstore.Core.Data;

namespace Sattleme.Bitcoin.Services;

/// <summary>
/// The module's records in the shop's database. Every lock and every claim is one conditional
/// UPDATE (EF Core's ExecuteUpdate: it changes the row only when the condition holds, and says whether
/// it did), atomic on every database Smartstore runs on: two requests, or two servers, never both hold
/// the order's lock, the check of an invoice, or its completion. Reads never come from EF's change
/// tracker (AsNoTracking): each one sees what the database holds now.
/// </summary>
public class SattlemeStore
{
    private readonly SmartDbContext _db;

    /// <summary>The records of the database <paramref name="db"/> reaches.</summary>
    public SattlemeStore(SmartDbContext db)
    {
        _db = db;
    }

    #region Orders

    /// <summary>The record of the order <paramref name="orderId"/>, or null.</summary>
    public virtual Task<SattlemeOrder?> GetOrderAsync(int orderId)
    {
        return _db.SattlemeOrders().AsNoTracking().FirstOrDefaultAsync(order => order.OrderId == orderId)!;
    }

    /// <summary>The record of the order of GUID <paramref name="orderGuid"/>, or null.</summary>
    public virtual Task<SattlemeOrder?> GetOrderByGuidAsync(Guid orderGuid)
    {
        return _db.SattlemeOrders().AsNoTracking().FirstOrDefaultAsync(order => order.OrderGuid == orderGuid)!;
    }

    /// <summary>The records of the orders of <paramref name="customerId"/>.</summary>
    public virtual Task<List<SattlemeOrder>> GetOrdersOfCustomerAsync(int customerId)
    {
        return _db.SattlemeOrders().AsNoTracking().Where(order => order.CustomerId == customerId).ToListAsync();
    }

    /// <summary>
    /// The record of the order <paramref name="orderId"/>, made (with the secret of its payment page)
    /// when it has none. Two requests making it at once get the same one.
    /// </summary>
    public virtual async Task<SattlemeOrder> GetOrMakeOrderAsync(int orderId, Guid orderGuid, int storeId, int customerId, DateTime now)
    {
        var existing = await GetOrderAsync(orderId);
        if (existing is not null)
        {
            return existing;
        }

        var made = new SattlemeOrder
        {
            OrderId = orderId,
            OrderGuid = orderGuid,
            StoreId = storeId,
            CustomerId = customerId,
            PageToken = NewSecret(32),
            CreatedOnUtc = now,
        };
        _db.SattlemeOrders().Add(made);
        try
        {
            await _db.SaveChangesAsync();
            return made;
        }
        catch (DbUpdateException)
        {
            // Another request made it first (the order's identifier is unique): that one.
            return await GetOrderAsync(orderId) ?? throw new InvalidOperationException("The order's record could not be made");
        }
        finally
        {
            _db.Entry(made).State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Takes the lock of the order <paramref name="orderId"/> for <paramref name="lockId"/> until
    /// <paramref name="until"/>, unless another holder has it (until a time after <paramref name="now"/>).
    /// </summary>
    public virtual async Task<bool> TryLockOrderAsync(int orderId, string lockId, DateTime now, DateTime until)
    {
        return await _db.SattlemeOrders()
            .Where(order => order.OrderId == orderId && (order.LockedUntilUtc == null || order.LockedUntilUtc < now))
            .ExecuteUpdateAsync(set => set
                .SetProperty(order => order.LockId, lockId)
                .SetProperty(order => order.LockedUntilUtc, until)) == 1;
    }

    /// <summary>Releases the lock <paramref name="lockId"/> holds on the order (nothing when it no longer holds it).</summary>
    public virtual async Task UnlockOrderAsync(int orderId, string lockId)
    {
        await _db.SattlemeOrders()
            .Where(order => order.OrderId == orderId && order.LockId == lockId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(order => order.LockId, (string?)null)
                .SetProperty(order => order.LockedUntilUtc, (DateTime?)null));
    }

    #endregion

    #region Invoices

    /// <summary>The record <paramref name="id"/>, or null.</summary>
    public virtual Task<SattlemeInvoice?> GetInvoiceAsync(int id)
    {
        return _db.SattlemeInvoices().AsNoTracking().FirstOrDefaultAsync(invoice => invoice.Id == id)!;
    }

    /// <summary>The invoices of the order <paramref name="orderId"/>, oldest first.</summary>
    public virtual Task<List<SattlemeInvoice>> GetInvoicesAsync(int orderId)
    {
        return _db.SattlemeInvoices().AsNoTracking()
            .Where(invoice => invoice.OrderId == orderId)
            .OrderBy(invoice => invoice.Id)
            .ToListAsync();
    }

    /// <summary>Stores a new try of an order, its reference made: before the SDK is asked for an invoice of it.</summary>
    public virtual async Task InsertInvoiceAsync(SattlemeInvoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        _db.SattlemeInvoices().Add(invoice);
        try
        {
            await _db.SaveChangesAsync();
        }
        finally
        {
            _db.Entry(invoice).State = EntityState.Detached;
        }
    }

    /// <summary>
    /// Stores the invoice the SDK made for the try <paramref name="id"/>, unless it has one: its
    /// checks start at <paramref name="nextCheck"/>.
    /// </summary>
    public virtual async Task<bool> StoreMadeInvoiceAsync(int id, string invoiceJson, long expiresAt, DateTime nextCheck)
    {
        return await _db.SattlemeInvoices()
            .Where(invoice => invoice.Id == id && invoice.InvoiceJson == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(invoice => invoice.InvoiceJson, invoiceJson)
                .SetProperty(invoice => invoice.ExpiresAt, expiresAt)
                .SetProperty(invoice => invoice.NextCheckOnUtc, nextCheck)
                .SetProperty(invoice => invoice.Error, "")) == 1;
    }

    /// <summary>Why the try <paramref name="id"/> made no invoice (an error's code).</summary>
    public virtual async Task SetMakeErrorAsync(int id, string code)
    {
        await _db.SattlemeInvoices()
            .Where(invoice => invoice.Id == id && invoice.InvoiceJson == null)
            .ExecuteUpdateAsync(set => set.SetProperty(invoice => invoice.Error, code));
    }

    /// <summary>
    /// Takes the check of the invoice <paramref name="id"/> for <paramref name="leaseId"/> until
    /// <paramref name="until"/>: only when it is made and not final, no other check holds it, and,
    /// with <paramref name="checkedBefore"/>, no check ended after that.
    /// </summary>
    public virtual async Task<bool> TryLeaseCheckAsync(int id, string leaseId, DateTime now, DateTime until, DateTime? checkedBefore)
    {
        var query = _db.SattlemeInvoices()
            .Where(invoice => invoice.Id == id && !invoice.Final && invoice.InvoiceJson != null
                && (invoice.LeaseUntilUtc == null || invoice.LeaseUntilUtc < now));
        if (checkedBefore is { } before)
        {
            query = query.Where(invoice => invoice.CheckedOnUtc == null || invoice.CheckedOnUtc <= before);
        }
        return await query.ExecuteUpdateAsync(set => set
            .SetProperty(invoice => invoice.LeaseId, leaseId)
            .SetProperty(invoice => invoice.LeaseUntilUtc, until)) == 1;
    }

    /// <summary>
    /// Stores what the check holding <paramref name="leaseId"/> found; false when it no longer holds
    /// the invoice (its lease ended and another check took it): it then decides nothing.
    /// </summary>
    public virtual async Task<bool> StoreStatusAsync(int id, string leaseId, string statusJson, string state, long received, DateTime now)
    {
        return await _db.SattlemeInvoices()
            .Where(invoice => invoice.Id == id && invoice.LeaseId == leaseId && !invoice.Final)
            .ExecuteUpdateAsync(set => set
                .SetProperty(invoice => invoice.StatusJson, statusJson)
                .SetProperty(invoice => invoice.State, state)
                .SetProperty(invoice => invoice.Received, received)
                .SetProperty(invoice => invoice.CheckedOnUtc, now)
                .SetProperty(invoice => invoice.Error, "")) == 1;
    }

    /// <summary>A check holding <paramref name="leaseId"/> failed with <paramref name="code"/>: one more failure in a row.</summary>
    public virtual async Task FailCheckAsync(int id, string leaseId, string code, DateTime now)
    {
        await _db.SattlemeInvoices()
            .Where(invoice => invoice.Id == id && invoice.LeaseId == leaseId)
            .ExecuteUpdateAsync(set => set
                .SetProperty(invoice => invoice.Error, code)
                .SetProperty(invoice => invoice.Errors, invoice => invoice.Errors + 1)
                .SetProperty(invoice => invoice.CheckedOnUtc, now)
                .SetProperty(invoice => invoice.LeaseId, (string?)null)
                .SetProperty(invoice => invoice.LeaseUntilUtc, (DateTime?)null));
    }

    /// <summary>
    /// Takes the completion of the paid invoice <paramref name="id"/> (marking its order paid) for
    /// the check holding <paramref name="leaseId"/>: unless one was taken after
    /// <paramref name="staleBefore"/> (it may still be under way).
    /// </summary>
    public virtual async Task<bool> ClaimCompletionAsync(int id, string leaseId, DateTime now, DateTime staleBefore)
    {
        return await _db.SattlemeInvoices()
            .Where(invoice => invoice.Id == id && invoice.LeaseId == leaseId && !invoice.Final
                && (invoice.CompletionClaimedOnUtc == null || invoice.CompletionClaimedOnUtc < staleBefore))
            .ExecuteUpdateAsync(set => set.SetProperty(invoice => invoice.CompletionClaimedOnUtc, now)) == 1;
    }

    /// <summary>
    /// Ends the check holding <paramref name="leaseId"/>: the invoice final (never checked again),
    /// or its next background check at <paramref name="nextCheck"/>; its failures in a row reset.
    /// </summary>
    public virtual async Task ReleaseCheckAsync(int id, string leaseId, bool final, DateTime nextCheck)
    {
        var held = _db.SattlemeInvoices().Where(invoice => invoice.Id == id && invoice.LeaseId == leaseId);
        if (final)
        {
            await held.ExecuteUpdateAsync(set => set
                .SetProperty(invoice => invoice.Final, true)
                .SetProperty(invoice => invoice.NextCheckOnUtc, (DateTime?)null)
                .SetProperty(invoice => invoice.Errors, 0)
                .SetProperty(invoice => invoice.LeaseId, (string?)null)
                .SetProperty(invoice => invoice.LeaseUntilUtc, (DateTime?)null));
        }
        else
        {
            await held.ExecuteUpdateAsync(set => set
                .SetProperty(invoice => invoice.NextCheckOnUtc, nextCheck)
                .SetProperty(invoice => invoice.Errors, 0)
                .SetProperty(invoice => invoice.LeaseId, (string?)null)
                .SetProperty(invoice => invoice.LeaseUntilUtc, (DateTime?)null));
        }
    }

    /// <summary>When the background checks look at the invoice <paramref name="id"/> next (unless it is final).</summary>
    public virtual async Task SetNextCheckAsync(int id, DateTime nextCheck)
    {
        await _db.SattlemeInvoices()
            .Where(invoice => invoice.Id == id && !invoice.Final)
            .ExecuteUpdateAsync(set => set.SetProperty(invoice => invoice.NextCheckOnUtc, nextCheck));
    }

    /// <summary>The invoices whose background check is due at <paramref name="now"/>, the longest due first.</summary>
    public virtual Task<List<SattlemeInvoice>> GetDueAsync(DateTime now, int take)
    {
        return _db.SattlemeInvoices().AsNoTracking()
            .Where(invoice => !invoice.Final && invoice.InvoiceJson != null && invoice.NextCheckOnUtc != null && invoice.NextCheckOnUtc <= now)
            .OrderBy(invoice => invoice.NextCheckOnUtc)
            .Take(take)
            .ToListAsync();
    }

    /// <summary>The invoices made before <paramref name="madeBefore"/> whose final status is still unknown.</summary>
    public virtual Task<List<SattlemeInvoice>> GetUnsettledMadeBeforeAsync(DateTime madeBefore, int take)
    {
        return _db.SattlemeInvoices().AsNoTracking()
            .Where(invoice => !invoice.Final && invoice.InvoiceJson != null && invoice.CreatedOnUtc < madeBefore)
            .OrderBy(invoice => invoice.CreatedOnUtc)
            .Take(take)
            .ToListAsync();
    }

    /// <summary>How many invoices are made and not final: a payment may still come for each.</summary>
    public virtual Task<int> CountUnsettledAsync()
    {
        return _db.SattlemeInvoices().CountAsync(invoice => !invoice.Final && invoice.InvoiceJson != null);
    }

    /// <summary>Gives up on the invoice <paramref name="id"/> (no final status after 90 days); false when it was final already.</summary>
    public virtual async Task<bool> GiveUpAsync(int id)
    {
        return await _db.SattlemeInvoices()
            .Where(invoice => invoice.Id == id && !invoice.Final)
            .ExecuteUpdateAsync(set => set
                .SetProperty(invoice => invoice.Final, true)
                .SetProperty(invoice => invoice.Error, "abandoned")
                .SetProperty(invoice => invoice.NextCheckOnUtc, (DateTime?)null)) == 1;
    }

    #endregion

    #region Personal data

    /// <summary>Marks the invoices of the orders <paramref name="orderIds"/> as their customer's data removed.</summary>
    public virtual async Task MarkShreddedAsync(IReadOnlyCollection<int> orderIds)
    {
        ArgumentNullException.ThrowIfNull(orderIds);
        if (orderIds.Count == 0)
        {
            return;
        }
        await _db.SattlemeInvoices()
            .Where(invoice => orderIds.Contains(invoice.OrderId))
            .ExecuteUpdateAsync(set => set.SetProperty(invoice => invoice.Shredded, true));
    }

    /// <summary>Replaces what a final invoice keeps (its JSON and its last status) with what is left once shredded.</summary>
    public virtual async Task ReplaceFinalDataAsync(int id, string? invoiceJson, string? statusJson)
    {
        await _db.SattlemeInvoices()
            .Where(invoice => invoice.Id == id && invoice.Final && invoice.Shredded)
            .ExecuteUpdateAsync(set => set
                .SetProperty(invoice => invoice.InvoiceJson, invoiceJson)
                .SetProperty(invoice => invoice.StatusJson, statusJson));
    }

    #endregion

    /// <summary><paramref name="bytes"/> bytes of the operating system's CSPRNG, in hex.</summary>
    public static string NewSecret(int bytes)
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));
    }
}
