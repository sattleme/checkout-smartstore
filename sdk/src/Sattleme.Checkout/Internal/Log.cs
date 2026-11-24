using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Sattleme.Checkout.Internal;

/// <summary>
/// Where the package's warnings go (SPEC.md §4: warnings only, never a secret): a newer format met,
/// a failure only logged, a rate source replaced by the next one. Each starts with
/// <c>Sattleme.Checkout:</c>.
/// </summary>
internal class Warnings(ILogger logger)
{
    private static readonly Action<ILogger, string, Exception?> Warning =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1, "SattlemeCheckoutWarning"), "Sattleme.Checkout: {Message}");

    /// <summary>Where the warnings go.</summary>
    public ILogger Logger { get; } = logger;

    /// <summary>Logs <paramref name="message"/> as a warning.</summary>
    public virtual void Warn(string message) => Warning(Logger, message, null);
}

/// <summary>
/// The warnings of the checks of one WaitForPaymentAsync (SPEC.md §23): a warning each check meets
/// again (an explorer or a Lightning address that does not answer, a rest invoice that cannot be
/// asked, a witness that cannot be published) is logged once, and again only once a check went
/// without it.
/// </summary>
internal sealed class OnceWarnings(ILogger logger) : Warnings(logger)
{
    private readonly object _gate = new();
    private HashSet<string> _before = new(StringComparer.Ordinal);
    private HashSet<string> _now = new(StringComparer.Ordinal);

    public override void Warn(string message)
    {
        bool fresh;
        lock (_gate)
        {
            fresh = !_before.Contains(message) && !_now.Contains(message);
            _now.Add(message);
        }
        if (fresh)
        {
            base.Warn(message);
        }
    }

    /// <summary>Warns of a check that failed (a network error): it is checked again.</summary>
    public void Failed(NetworkException error) => Warn($"the payment check failed ({error.Message}); checked again");

    /// <summary>A check ended: a warning it did not meet stopped.</summary>
    public void Checked()
    {
        lock (_gate)
        {
            _before = _now;
            _now = new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
