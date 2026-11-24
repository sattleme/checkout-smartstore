using System.Globalization;
using Sattleme.Bitcoin.Configuration;
using Sattleme.Bitcoin.Controllers;
using Sattleme.Bitcoin.Services;
using Sattleme.Bitcoin.Tasks;
using Smartstore.Engine.Modularity;
using Smartstore.Http;
using Smartstore.Scheduling;

namespace Sattleme.Bitcoin;

/// <summary>
/// The module's installation (Smartstore installs a module when the application starts again, its
/// migrations having made its tables): its settings, its texts in each of the store's languages, and
/// the scheduled task of its background checks.
/// </summary>
internal sealed class Module : ModuleBase, IConfigurable
{
    private readonly SattlemeStore _store;
    private readonly ITaskStore _taskStore;

    public Module(SattlemeStore store, ITaskStore taskStore)
    {
        _store = store;
        _taskStore = taskStore;
    }

    /// <summary>The configuration page (the module's, and its payment method's).</summary>
    public RouteInfo GetConfigurationRoute()
        => new(nameof(SattlemeAdminController.Configure), "SattlemeAdmin", new { area = "Admin" });

    /// <inheritdoc/>
    public override async Task InstallAsync(ModuleInstallationContext context)
    {
        await TrySaveSettingsAsync<SattlemeSettings>();
        await ImportLanguageResourcesAsync();
        await _taskStore.GetOrAddTaskAsync<SattlemeCheckTask>(task =>
        {
            task.Name = "Check the Bitcoin payments (sattle.me)";
            task.CronExpression = SattlemeDefaults.CheckTaskCron;
            task.Enabled = true;
            task.StopOnError = false;
        });
        await base.InstallAsync(context);
    }

    /// <summary>
    /// Refuses while a payment may still come (an invoice that can be paid, or a payment being
    /// confirmed): uninstalled, the module checks no payment. Otherwise removes the settings (the
    /// integration key included), the texts and the scheduled task; the invoices stay in their tables,
    /// as Smartstore's guidelines ask.
    /// </summary>
    public override async Task UninstallAsync()
    {
        var pending = await _store.CountUnsettledAsync();
        if (pending > 0)
        {
            // One invoice in the singular, more in the plural (Smartstore's resources have no plural forms).
            var format = Services.Localization.GetResource(SattlemeDefaults.ResourcePrefix + (pending == 1 ? "Uninstall.PendingOne" : "Uninstall.Pending"));
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, format, pending));
        }
        await _taskStore.TryDeleteTaskAsync<SattlemeCheckTask>();
        await DeleteSettingsAsync<SattlemeSettings>();
        await DeleteLanguageResourcesAsync();
        // The names Smartstore reads (Plugins.FriendlyName.{SystemName}, Plugins.Description.…) are
        // resources of their own, not under the root key: deleted by name (DeleteLanguageResourcesAsync
        // deletes what is under a key, "key.%", never the key itself).
        foreach (var name in (string[])["Plugins.FriendlyName." + SattlemeDefaults.ModuleSystemName, "Plugins.Description." + SattlemeDefaults.ModuleSystemName, "Plugins.FriendlyName." + SattlemeDefaults.SystemName])
        {
            await Services.Localization.DeleteLocaleStringResourcesAsync(name, keyIsRootKey: false);
        }
        await base.UninstallAsync();
    }
}
