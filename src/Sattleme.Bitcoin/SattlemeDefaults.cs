namespace Sattleme.Bitcoin;

/// <summary>
/// The module's names and rules: its system names, routes, resources, and how long and how often it
/// waits (README.md, "Background checks").
/// </summary>
public static class SattlemeDefaults
{
    #region Names

    /// <summary>The module's system name, and its folder under Smartstore's Modules/.</summary>
    public const string ModuleSystemName = "Sattleme.Bitcoin";

    /// <summary>The payment method's system name (the provider's).</summary>
    public const string SystemName = "Payments.Sattleme";

    /// <summary>The module's static files (the payment page's script and style), under its wwwroot.</summary>
    public const string Content = "~/Modules/Sattleme.Bitcoin/";

    /// <summary>The root of the module's locale resources (module.json's ResourceRootKey).</summary>
    public const string ResourcePrefix = "Plugins.Sattleme.Bitcoin.";

    /// <summary>The setting that holds the integration key, encrypted, per store.</summary>
    public const string KeySetting = "SattlemeSettings.EncryptedIntegrationKey";

    /// <summary>The order reference of each invoice: this prefix, then 128 random bits in hex.</summary>
    public const string ReferencePrefix = "ss-";

    /// <summary>The name of the admin's tab of an order's Bitcoin payment.</summary>
    public const string AdminTabName = "tab-sattleme";

    /// <summary>Where the customer's order page shows its Bitcoin payment.</summary>
    public const string OrderDetailsZone = "orderdetails_page_overview";

    #endregion

    #region Invoices

    /// <summary>How many minutes the customer has to pay an invoice: the bounds of the setting, and its default.</summary>
    public const int MinMinutes = 1;

    /// <inheritdoc cref="MinMinutes"/>
    public const int MaxMinutes = 60;

    /// <inheritdoc cref="MinMinutes"/>
    public const int DefaultMinutes = 15;

    /// <summary>
    /// The SDK may report a payment made in an invoice's last seconds up to this long after its end
    /// (the specification's grace period, in seconds): an invoice may be paid until then.
    /// </summary>
    public const int GraceSeconds = 120;

    /// <summary>Per relay request and per HTTP request of the SDK, connection and body included, in milliseconds.</summary>
    public const int RelayTimeoutMs = 5_000;

    /// <inheritdoc cref="RelayTimeoutMs"/>
    public const int HttpTimeoutMs = 8_000;

    /// <summary>Making an invoice, in the customer's request.</summary>
    public static readonly TimeSpan CreateTimeout = TimeSpan.FromSeconds(25);

    /// <summary>Verifying a key the admin enters.</summary>
    public static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(15);

    /// <summary>One check of a payment.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A request that waited longer than this for another one making the order's invoice, which made
    /// none, makes none either: two calls to the SDK in a row could outlast the request.
    /// </summary>
    public static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

    /// <summary>A lock or a check holds its record this much longer than its call to the SDK may take.</summary>
    public static readonly TimeSpan LeaseMargin = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A completion (the order marked paid) claimed this long ago and not done stopped half-way (the
    /// process ended): the next check takes it again.
    /// </summary>
    public static readonly TimeSpan CompletionStale = TimeSpan.FromMinutes(10);

    #endregion

    #region Checks

    /// <summary>The scheduled task's cron expression: every minute, as often as Smartstore's scheduler polls.</summary>
    public const string CheckTaskCron = "* * * * *";

    /// <summary>Between two background checks of an invoice that can still be paid.</summary>
    public static readonly TimeSpan OpenInterval = TimeSpan.FromSeconds(60);

    /// <summary>Between two background checks of an invoice past its end whose final status is not known yet.</summary>
    public static readonly TimeSpan SettlingInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The last check of an invoice's grace period is due this long before its end, and the first one
    /// after it this long after.
    /// </summary>
    public static readonly TimeSpan EndMargin = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A run of the scheduled task may start up to this much less than a minute after the last one
    /// (Smartstore's scheduler polls every minute, DefaultTaskScheduler's timer, and the run starts
    /// once the request it makes reaches the task): an invoice a run checked is due again a wait
    /// after that run's start less this, so that the run a wait later checks it
    /// (PaymentRules.NextCheck).
    /// </summary>
    public static readonly TimeSpan RunSlack = TimeSpan.FromSeconds(5);

    /// <summary>The customer's payment page has its invoice checked at most this often.</summary>
    public static readonly TimeSpan PageInterval = TimeSpan.FromSeconds(5);

    /// <summary>After failed checks, the next one waits twice as long each time, up to this.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    /// <summary>
    /// An invoice whose final status is still unknown this long after it was made is no longer checked
    /// (an on-chain payment unconfirmed for months): its order is then settled by hand.
    /// </summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(90);

    /// <summary>
    /// One run of the scheduled task checks invoices for this long at most: Smartstore's scheduler
    /// polls every minute and skips a task still running (TaskExecutor.cs, its last execution
    /// IsRunning).
    /// </summary>
    public static readonly TimeSpan TaskBudget = TimeSpan.FromSeconds(45);

    /// <summary>Invoices checked per run of the scheduled task, at most.</summary>
    public const int TaskBatch = 20;

    #endregion
}
