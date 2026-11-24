using Sattleme.Bitcoin.Services;
using Smartstore.Core.Data;
using Smartstore.Core.Identity;
using Smartstore.Data.Hooks;

namespace Sattleme.Bitcoin.Hooks;

/// <summary>
/// A customer deleted in the admin area (Smartstore deletes a registered customer softly, keeping
/// their orders, and then removes their IP address: Identity/Services/UserStore.cs, its own hook on
/// the same deletion): their invoices lose what leads to their coins, as when they are anonymized.
/// </summary>
public class CustomerDeletedHook : AsyncDbSaveHook<Customer>
{
    private readonly PersonalDataShredder _shredder;

    /// <summary>The hook.</summary>
    public CustomerDeletedHook(PersonalDataShredder shredder)
    {
        _shredder = shredder;
    }

    /// <inheritdoc/>
    protected override Task<HookResult> OnUpdatedAsync(Customer entity, IHookedEntity entry, CancellationToken cancelToken)
        => Task.FromResult(HookResult.Ok);

    /// <inheritdoc/>
    public override async Task OnAfterSaveCompletedAsync(IEnumerable<IHookedEntity> entries, CancellationToken cancelToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
        {
            if (entry.IsSoftDeleted == true && entry.Entity is Customer customer)
            {
                await _shredder.ShredCustomerAsync(customer.Id);
            }
        }
    }
}
