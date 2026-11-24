using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Sattleme.Bitcoin.Configuration;
using Sattleme.Bitcoin.Controllers;
using Sattleme.Bitcoin.Models;
using Sattleme.Bitcoin.Services;
using Sattleme.Bitcoin.Tests.Support;
using Smartstore.Core.Logging;
using Smartstore.Core.Security;
using Smartstore.Web.Modelling.Settings;
using SmartstoreSattleme.Sattleme.Checkout;

namespace Sattleme.Bitcoin.Tests;

/// <summary>
/// The configuration page: the key verified when saved, stored encrypted per store scope, never
/// sent back to the browser; the time to pay; what the page says of the key and the currency.
/// </summary>
[TestFixture]
public sealed class SettingsTests
{
    private const string NewKey = "sattleme_ck_qEVejHuCZpF4XMmBSw5p7iZYxCtm20XxzT1fnJ9VB8o.uPTAz1d8bbQaw2Jx22Wwrga2vI2F8allY5NrPC3Aogk.cmVsYXkyLmV4YW1wbGU";
    private Shop _shop = null!;
    private Scope? _last;

    [SetUp]
    public async Task SetUp()
    {
        _shop = new Shop();
        await _shop.NewScope().Keys.RemoveAsync(0);
    }

    [TearDown]
    public void TearDown()
    {
        _shop.Dispose();
    }

    private SattlemeAdminController Controller(int storeScope = 0)
    {
        _last = _shop.NewScope();
        return _last.AdminController(storeScope);
    }

    private static ConfigurationModel Posted(string? key = null, int minutes = 15)
    {
        return new ConfigurationModel { IntegrationKey = key, MinutesToPay = minutes };
    }

    /// <summary>
    /// Posts the page as Smartstore runs it: its [SaveSetting] filter gives the action the settings
    /// of the store scope, then saves them when the model state is valid (SaveSettingFilter).
    /// </summary>
    private async Task<IActionResult> PostAsync(ConfigurationModel model, int storeScope = 0, Action<SattlemeAdminController>? before = null)
    {
        var controller = Controller(storeScope);
        before?.Invoke(controller);
        var settings = await _last!.SettingFactory.LoadSettingsAsync<SattlemeSettings>(storeScope);
        var result = await controller.Configure(model, settings);
        if (controller.ModelState.IsValid)
        {
            await _last.SettingFactory.SaveSettingsAsync(settings, storeScope);
        }
        return result;
    }

    private async Task<ConfigurationModel> PageAsync(int storeScope = 0)
    {
        var controller = Controller(storeScope);
        var settings = await _last!.SettingFactory.LoadSettingsAsync<SattlemeSettings>(storeScope);
        return (ConfigurationModel)((ViewResult)await controller.Configure(settings)).Model!;
    }

    private string Text(string name)
    {
        return _shop.Texts.Values[SattlemeDefaults.ResourcePrefix + name];
    }

    private string? StoredKey(int storeId)
    {
        using var db = _shop.Database.NewContext();
        return db.Settings.Where(setting => setting.Name == SattlemeDefaults.KeySetting && setting.StoreId == storeId).Select(setting => setting.Value).SingleOrDefault();
    }

    [Test]
    public async Task Saves_a_verified_key_encrypted_and_never_sends_it_back()
    {
        var result = await PostAsync(Posted("  " + NewKey + "  "), before: controller =>
            controller.ModelState.SetModelValue(nameof(ConfigurationModel.IntegrationKey), NewKey, NewKey));

        Assert.That(result, Is.InstanceOf<RedirectToActionResult>(), "shown again by a GET: nothing entered goes back");
        Assert.That(_shop.Gateway.Merchants, Is.EqualTo(1), "verified with the SDK");
        var stored = StoredKey(0);
        Assert.That(stored, Is.Not.Empty.And.Not.Contains(NewKey));
        Assert.That(_shop.Encryptor().DecryptText(stored!), Is.EqualTo(NewKey), "Smartstore's encryptor, with the store's encryption key");
        Assert.That(_last!.Notifier.Entries.Single(entry => entry.Type == NotifyType.Success).Message,
            Is.EqualTo(string.Format(System.Globalization.CultureInfo.CurrentCulture, Text("Key.Verified"), "Sunrise Bakery")));
        Assert.That(_shop.AllLogged(), Does.Not.Contain(NewKey));
        Assert.That(await _shop.NewScope().Keys.KeyOfStoreAsync(TestDatabase.StoreId), Is.EqualTo(NewKey));

        var page = await PageAsync();
        Assert.That(page.IntegrationKey, Is.Null);
        Assert.That(page.HasKey, Is.True);
        Assert.That(page.KeyStatus, Is.EqualTo(Text("Key.Saved")));
        Assert.That(JsonSerializer.Serialize(page), Does.Not.Contain(NewKey).And.Not.Contain(stored!));
    }

    private static IEnumerable<TestCaseData> Refusals()
    {
        yield return new TestCaseData("other_ck_" + new string('a', 40), null, "Key.Invalid").SetName("Refuses_what_is_not_an_integration_key");
        yield return new TestCaseData(NewKey + "x", null, "Key.Invalid").SetName("Refuses_a_damaged_key");
        yield return new TestCaseData(NewKey, new RevokedException(), "Key.Revoked").SetName("Refuses_a_removed_integration");
        yield return new TestCaseData(NewKey, new ChannelNotFoundException(false), "Key.NotFound").SetName("Refuses_an_integration_not_found");
        yield return new TestCaseData(NewKey, new ChannelNotFoundException(true), "Key.Newer").SetName("Refuses_an_integration_of_a_newer_format");
        yield return new TestCaseData(NewKey, new NetworkException("relays unreachable"), "Key.Unverified").SetName("Refuses_a_key_it_could_not_verify");
        yield return new TestCaseData(NewKey, new OperationCanceledException(), "Key.Unverified").SetName("Refuses_a_key_whose_verification_took_too_long");
    }

    [TestCaseSource(nameof(Refusals))]
    public async Task Refuses_a_key_with_why_in_plain_words_and_never_echoes_it(string key, Exception? error, string message)
    {
        if (error is not null)
        {
            _shop.Gateway.Merchant = _ => throw error;
        }
        SattlemeAdminController? posted = null;
        var result = await PostAsync(Posted(key), before: controller =>
        {
            posted = controller;
            controller.ModelState.SetModelValue(nameof(ConfigurationModel.IntegrationKey), key, key);
        });

        var view = (ViewResult)result;
        var model = (ConfigurationModel)view.Model!;
        Assert.That(model.IntegrationKey, Is.Null);
        var entry = posted!.ModelState[nameof(ConfigurationModel.IntegrationKey)]!;
        Assert.That(entry.RawValue, Is.Null);
        Assert.That(entry.AttemptedValue, Is.Null);
        Assert.That(entry.Errors.Single().ErrorMessage, Is.EqualTo(Text(message)));
        Assert.That(JsonSerializer.Serialize(model), Does.Not.Contain(key));
        Assert.That(StoredKey(0), Is.Null, "nothing saved");
        Assert.That(_shop.AllLogged(), Does.Not.Contain(key));
    }

    [Test]
    public async Task Verifies_a_key_with_the_real_SDK_which_refuses_an_invalid_one()
    {
        var controller = _shop.NewScope().AdminController(gateway: new SattlemeGateway(NullLoggerFactory.Instance));
        var result = await controller.Configure(Posted("sattleme_ck_not-a-key"), new SattlemeSettings());

        Assert.That(((ConfigurationModel)((ViewResult)result).Model!).IntegrationKey, Is.Null);
        Assert.That(controller.ModelState[nameof(ConfigurationModel.IntegrationKey)]!.Errors.Single().ErrorMessage, Is.EqualTo(Text("Key.Invalid")));
        Assert.That(StoredKey(0), Is.Null);
    }

    [TestCase(0)]
    [TestCase(61)]
    [TestCase(-5)]
    public async Task Takes_a_time_to_pay_of_1_to_60_minutes(int minutes)
    {
        SattlemeAdminController? posted = null;
        var result = await PostAsync(Posted(minutes: minutes), before: controller => posted = controller);

        Assert.That(result, Is.InstanceOf<ViewResult>());
        Assert.That(posted!.ModelState[nameof(ConfigurationModel.MinutesToPay)]!.Errors.Single().ErrorMessage, Is.EqualTo(Text("MinutesToPay.Range")));
        Assert.That((await PageAsync()).MinutesToPay, Is.EqualTo(15), "nothing saved");
    }

    [TestCase(1)]
    [TestCase(60)]
    public async Task Saves_the_time_to_pay(int minutes)
    {
        await PostAsync(Posted(minutes: minutes));

        Assert.That((await PageAsync()).MinutesToPay, Is.EqualTo(minutes));
    }

    [Test]
    public async Task Saves_a_key_for_one_store_which_takes_it_over_every_store_s()
    {
        await PostAsync(Posted(SdkData.KEY));
        await PostAsync(Posted(NewKey), storeScope: TestDatabase.StoreId);

        Assert.That(StoredKey(0), Is.Not.Null);
        Assert.That(StoredKey(TestDatabase.StoreId), Is.Not.Null);
        Assert.That(await _shop.NewScope().Keys.KeyOfStoreAsync(TestDatabase.StoreId), Is.EqualTo(NewKey));
        Assert.That((await PageAsync(TestDatabase.StoreId)).KeyStatus, Is.EqualTo(Text("Key.SavedForStore")));

        // Its own removed, the store has every store's again.
        var model = Posted();
        model.RemoveKey = true;
        await PostAsync(model, storeScope: TestDatabase.StoreId);
        Assert.That(StoredKey(TestDatabase.StoreId), Is.Null);
        Assert.That(await _shop.NewScope().Keys.KeyOfStoreAsync(TestDatabase.StoreId), Is.EqualTo(SdkData.KEY));
        Assert.That((await PageAsync(TestDatabase.StoreId)).KeyStatus, Is.EqualTo(Text("Key.SavedForAll")));
    }

    [Test]
    public async Task Removes_the_key_when_asked()
    {
        await PostAsync(Posted(NewKey));
        var model = Posted();
        model.RemoveKey = true;
        await PostAsync(model);

        Assert.That(StoredKey(0), Is.Null);
        var page = await PageAsync();
        Assert.That(page.KeyStatus, Is.EqualTo(Text("Key.None")));
        Assert.That(page.HasKey, Is.False);
    }

    [Test]
    public async Task Says_when_the_saved_key_cannot_be_read()
    {
        // Smartstore's admin can change the encryption key (Settings → General → Security): the
        // saved key is not encrypted again with it.
        await PostAsync(Posted(NewKey));
        _shop.EncryptionKey = "1414213562373095";
        var page = await PageAsync();

        Assert.That(page.KeyStatus, Is.EqualTo(Text("Key.Unreadable")));
        Assert.That(page.HasKey, Is.False);
        Assert.That(await _shop.NewScope().Keys.KeyOfStoreAsync(TestDatabase.StoreId), Is.Null);
    }

    [Test]
    public async Task Warns_when_the_store_s_currency_cannot_be_converted()
    {
        _shop.PrimaryCurrency = "XAU";
        var page = await PageAsync();

        Assert.That(page.CurrencyWarning, Is.EqualTo(Text("Currency.Unsupported")));
        Assert.That(page.PrimaryCurrencyCode, Is.EqualTo("XAU"));
    }

    [Test]
    public void Only_admins_who_manage_payment_methods_see_the_page_and_every_change_posts_its_antiforgery_token()
    {
        var type = typeof(SattlemeAdminController);
        Assert.That(type.IsSubclassOf(typeof(global::Smartstore.Web.Controllers.AdminController)), Is.True, "Smartstore's admin area: AuthorizeAdmin, AutoValidateAntiforgeryToken");
        Assert.That(type.GetCustomAttribute<AreaAttribute>(true)!.RouteValue, Is.EqualTo("Admin"));
        Assert.That(type.GetCustomAttribute<AutoValidateAntiforgeryTokenAttribute>(true), Is.Not.Null);
        var actions = type.GetMethods().Where(method => method.Name == nameof(SattlemeAdminController.Configure)).ToList();
        Assert.That(actions, Has.Count.EqualTo(2));
        var get = actions.Single(action => action.GetParameters().Length == 1);
        var post = actions.Single(action => action.GetParameters().Length == 2);
        Assert.That(Permission(get), Is.EqualTo(Permissions.Configuration.PaymentMethod.Read));
        Assert.That(Permission(post), Is.EqualTo(Permissions.Configuration.PaymentMethod.Update));
        Assert.That(post.GetCustomAttribute<HttpPostAttribute>(), Is.Not.Null);
        Assert.That(post.GetCustomAttribute<SaveSettingAttribute>(), Is.Not.Null);
        Assert.That(get.GetCustomAttribute<LoadSettingAttribute>(), Is.Not.Null);
        Assert.That(Permission(type.GetMethod(nameof(SattlemeAdminController.OrderPayment))!), Is.EqualTo(Permissions.Order.Read));
    }

    private static string? Permission(MethodInfo action)
    {
        var permission = action.GetCustomAttribute<PermissionAttribute>();
        var requirement = permission?.Arguments?.Single();
        return (string?)requirement?.GetType().GetProperty("SystemName")!.GetValue(requirement);
    }

    [Test]
    public void The_key_field_is_a_password_field_of_a_model_that_never_holds_the_saved_key()
    {
        var property = typeof(ConfigurationModel).GetProperty(nameof(ConfigurationModel.IntegrationKey))!;
        Assert.That(property.GetCustomAttribute<System.ComponentModel.DataAnnotations.DataTypeAttribute>()!.DataType,
            Is.EqualTo(System.ComponentModel.DataAnnotations.DataType.Password));
        Assert.That(typeof(ConfigurationModel).GetProperties().Select(p => p.Name), Has.None.Contains("Encrypted"));
        Assert.That(typeof(SattlemeSettings).GetProperties().Select(p => p.Name), Has.None.Contains("Key"), "the key is never a property the settings page binds");
        var view = File.ReadAllText(Path.Combine(Repository.Module, "Views", "SattlemeAdmin", "Configure.cshtml"));
        Assert.That(view, Does.Contain("<input asp-for=\"IntegrationKey\" type=\"password\" class=\"form-control\" value=\"\""));
    }
}
