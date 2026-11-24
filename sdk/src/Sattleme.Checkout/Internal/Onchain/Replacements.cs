using System;
using System.Collections.Generic;
using System.Linq;

namespace Sattleme.Checkout.Internal.Onchain;

/// <summary>A transaction paying the address, as the parts are made of it.</summary>
/// <param name="Txid">Its id.</param>
/// <param name="Sats">What it pays the address.</param>
/// <param name="Inputs">The coins it spends (<c>&lt;txid&gt;:&lt;vout&gt;</c>), or those recorded of it.</param>
/// <param name="Order">When it was seen (a witness's <c>created_at</c>); null: unknown, after every known one.</param>
/// <param name="Listed">Whether the explorer lists it now.</param>
internal sealed record PaymentTx(string Txid, long Sats, IReadOnlyList<string> Inputs, long? Order, bool Listed);

/// <summary>A part of a listed transaction: sats it pays as its <c>Root</c> (itself, for a part of its own).</summary>
internal readonly record struct Part(long Sats, string Root);

/// <summary>
/// Replacements (SPEC.md §31, "Replacements"): a payer whose transaction is slow to confirm may
/// broadcast another one in its place (RBF), which spends a coin of the same. A replacement keeps the
/// time of the payment it replaces, for at most what that payment paid the address: what it pays
/// more counts at its own time, as any other payment would.
/// </summary>
internal static class Replacements
{
    /// <summary>
    /// The parts of each listed transaction, by txid: a txid given twice is taken once (its first
    /// entry); the transactions in order (unknown last, then by txid); the roots of each are those
    /// before it that spend one of its coins (their roots, put together), else itself; each root gives
    /// at most its sats, in that order, then what a transaction still pays is a part of its own.
    /// </summary>
    public static Dictionary<string, List<Part>> PaymentParts(IEnumerable<PaymentTx> txs)
    {
        var unique = new Dictionary<string, PaymentTx>(StringComparer.Ordinal);
        var firstOrder = new List<PaymentTx>();
        foreach (var tx in txs)
        {
            if (unique.TryAdd(tx.Txid, tx))
            {
                firstOrder.Add(tx);
            }
        }
        var sorted = firstOrder.OrderBy(tx => tx.Order is null ? 1 : 0)
            .ThenBy(tx => tx.Order ?? 0)
            .ThenBy(tx => tx.Txid, StringComparer.Ordinal)
            .ToList();
        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < sorted.Count; i++)
        {
            position[sorted[i].Txid] = i;
        }
        var roots = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var index = 0; index < sorted.Count; index++)
        {
            var tx = sorted[index];
            var coins = new HashSet<string>(tx.Inputs, StringComparer.Ordinal);
            var together = new HashSet<string>(StringComparer.Ordinal);
            var before = false;
            for (var other = 0; other < index; other++)
            {
                if (sorted[other].Inputs.Any(coins.Contains))
                {
                    before = true;
                    together.UnionWith(roots[sorted[other].Txid]);
                }
            }
            roots[tx.Txid] = before ? [.. together.OrderBy(root => position[root])] : [tx.Txid];
        }
        var toGive = sorted.ToDictionary(tx => tx.Txid, tx => tx.Sats, StringComparer.Ordinal);
        var parts = new Dictionary<string, List<Part>>(StringComparer.Ordinal);
        foreach (var tx in sorted)
        {
            if (!tx.Listed)
            {
                continue;
            }
            var own = roots[tx.Txid];
            var owed = tx.Sats;
            var list = new List<Part>();
            if (own.Count == 1 && own[0] == tx.Txid)
            {
                // A payment of its own: all its sats, which it no longer has to give.
                toGive[tx.Txid] = 0;
            }
            else
            {
                foreach (var root in own)
                {
                    var left = toGive[root];
                    var take = Math.Min(owed, left);
                    if (take > 0)
                    {
                        list.Add(new Part(take, root));
                        toGive[root] = left - take;
                        owed -= take;
                    }
                }
            }
            if (owed > 0)
            {
                list.Add(new Part(owed, tx.Txid));
            }
            parts[tx.Txid] = list;
        }
        return parts;
    }
}
