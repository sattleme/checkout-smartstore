using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sattleme.Checkout.Internal.Crypto;

namespace Sattleme.Checkout.Internal.Nostr;

/// <summary>
/// What a read keeps of an event a relay sent (SPEC.md §13, "Limits"), as it comes: its id (a long
/// one by its SHA-256: an id is 64 characters), its date when it is a safe integer, and what the
/// read's <c>keep</c> keeps of it (null drops it). So what a relay sends never piles up.
/// </summary>
internal readonly record struct Entry(string? Id, long? CreatedAt, object? Kept)
{
    /// <summary>
    /// The entry of <paramref name="value"/>. <paramref name="read"/> holds the ids of the query so
    /// far: an event of one of them again (a copy, or a forgery: an id is its event's hash) only
    /// counts, its id and its date read, and is never kept nor checked again (SPEC.md §13, "Limits").
    /// </summary>
    public static Entry Of(Json? value, Func<Json, object?> keep, HashSet<string>? read = null)
    {
        var obj = value as JsonObject;
        var rawId = JsonRead.String(obj?["id"]);
        var id = rawId is null
            ? null
            : rawId.Length <= 64
                ? rawId
                // As JavaScript's TextEncoder writes it: a lone surrogate as U+FFFD.
                : "#" + Text.Hex(Hashes.Sha256(Encoding.UTF8.GetBytes(rawId)));
        var createdAt = JsonRead.SafeInteger(obj?["created_at"]);
        if (id is not null && read is not null && !read.Add(id))
        {
            return new Entry(id, createdAt, null);
        }
        return new Entry(id, createdAt, value is null ? null : keep(value));
    }
}

/// <summary>A refusal of an event by a relay (NIP-01 <c>OK</c> false), with its reason.</summary>
internal sealed class RelayRefusalException(string url, string reason) : Exception($"{url} refused the event: {reason}")
{
    public string Reason { get; } = reason;
}

/// <summary>Fewer relays than needed took a record.</summary>
internal sealed class QuorumException(string message) : Exception(message);

/// <summary>
/// What a read of a list gives (SPEC.md §13): the events of each relay that answered (its read
/// ended), and, on every relay, the pages that a relay still reading when the read went on had sent
/// (<paramref name="Partial"/>). Those are events, and that is all: such a relay did not answer.
/// </summary>
/// <param name="Answers">The events of each relay whose read ended, by relay.</param>
/// <param name="Partial">The pages of the relays still reading at the cut, by relay (reads on every relay only).</param>
internal record RelayAnswers(IReadOnlyDictionary<string, IReadOnlyList<object>> Answers, IReadOnlyDictionary<string, IReadOnlyList<object>> Partial)
{
    /// <summary>How many relays answered: those whose read ended, and nothing else.</summary>
    public int Answered => Answers.Count;

    /// <summary>The events every relay sent, each relay's apart: the answers', then the pages of the reads cut short.</summary>
    public IEnumerable<IReadOnlyList<object>> Events => Answers.Values.Concat(Partial.Values);
}

/// <summary>What a complete read of a list gives: the answers and pages of <see cref="RelayAnswers"/>, and how many relays read completely.</summary>
internal sealed record CompleteAnswers(IReadOnlyDictionary<string, IReadOnlyList<object>> Answers, IReadOnlyDictionary<string, IReadOnlyList<object>> Partial, int Complete)
    : RelayAnswers(Answers, Partial);

/// <summary>
/// What one relay's read of a list is (SPEC.md §13): still reading, ended (complete, or not: of a read
/// that is not a complete read, any read that ended is complete), or failed. Only a relay whose read
/// ended answered: one still reading has not, whatever pages it sent.
/// </summary>
internal enum RelayState
{
    Reading,
    Complete,
    Incomplete,
    Failed,
}

/// <summary>
/// Of a read of a list, from what each relay's read is (SPEC.md §13): how many answered (their read
/// ended, complete or not), how many read completely, and how many are over (answered or failed).
/// </summary>
internal readonly record struct Tally(int Answered, int Complete, int Settled)
{
    public static Tally Of(IEnumerable<RelayState> states)
    {
        int answered = 0, complete = 0, settled = 0;
        foreach (var state in states)
        {
            if (state is RelayState.Complete or RelayState.Incomplete)
            {
                answered++;
            }
            if (state == RelayState.Complete)
            {
                complete++;
            }
            if (state != RelayState.Reading)
            {
                settled++;
            }
        }
        return new Tally(answered, complete, settled);
    }
}

/// <summary>
/// One read of a list while it runs (SPEC.md §13): what each relay's read is, the events of each
/// relay whose read ended and the pages each relay still reading sent so far; when the read goes on,
/// and what it hands over then. <see cref="RelayPool"/>'s reads feed it as their relays answer, and
/// the vectors of relay-reads.json (<c>lists</c>, <c>onEveryRelay</c>) run through it.
/// </summary>
#pragma warning disable CA1001 // _stop holds no timer (cancelled, never disposed: the reads read its token once more); _grace is disposed when the read goes on.
internal sealed class ListRead
#pragma warning restore CA1001
{
    private readonly object _gate = new();
    private readonly int _needed;
    private readonly bool _everyRelay;
    private readonly bool _completeRead;
    private readonly int _graceMs;
    private readonly Dictionary<string, RelayState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<object>> _events = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<object>> _pages = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<RelayAnswers> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Cancelled once the read went on: the reads of the relays still reading stop. Never disposed: they read its token once more.</summary>
    private readonly CancellationTokenSource _stop = new();
    private Timer? _grace;
    private bool _done;

    /// <summary>
    /// A read of <paramref name="relays"/>, but those <paramref name="failedEarlier"/> gives true for
    /// (failed at once); it goes on once <paramref name="needed"/> relays answered (read completely,
    /// for a <paramref name="completeRead"/>), or on every relay (<paramref name="everyRelay"/>)
    /// <paramref name="graceMs"/> after.
    /// </summary>
    public ListRead(IReadOnlyList<string> relays, Func<string, bool> failedEarlier, int needed, bool everyRelay, bool completeRead, int graceMs)
    {
        _needed = needed;
        _everyRelay = everyRelay;
        _completeRead = completeRead;
        _graceMs = graceMs;
        var asked = new List<string>();
        foreach (var relay in relays)
        {
            var failed = failedEarlier(relay);
            _states[relay] = failed ? RelayState.Failed : RelayState.Reading;
            if (!failed)
            {
                asked.Add(relay);
                _pages[relay] = [];
            }
        }
        Asked = asked;
        Check();
    }

    /// <summary>The relays to read: not those that failed earlier.</summary>
    public IReadOnlyList<string> Asked { get; }

    /// <summary>What the read hands over once it goes on.</summary>
    public Task<RelayAnswers> Result => _result.Task;

    /// <summary>The token of the relays' reads: cancelled once the read went on.</summary>
    public CancellationToken Stop => _stop.Token;

    /// <summary>The pages <paramref name="relay"/> sent so far, which its read fills (under its own lock).</summary>
    public List<object> Pages(string relay) => _pages[relay];

    /// <summary>The read of <paramref name="relay"/> ended with <paramref name="events"/>, complete or not.</summary>
    public void Ended(string relay, IReadOnlyList<object> events, bool complete)
    {
        lock (_gate)
        {
            if (_done)
            {
                return;
            }
            _states[relay] = complete ? RelayState.Complete : RelayState.Incomplete;
            _events[relay] = events;
        }
        Check();
    }

    /// <summary>The read of <paramref name="relay"/> failed.</summary>
    public void Failed(string relay)
    {
        lock (_gate)
        {
            if (_done)
            {
                return;
            }
            _states[relay] = RelayState.Failed;
        }
        Check();
    }

    /// <summary>
    /// Goes on when the relays' reads say so (SPEC.md §13): once every relay answered or failed; on
    /// every relay of a complete read, as soon as a majority can no longer read completely; once
    /// enough answered (read completely), at once, or on every relay one timeout later.
    /// </summary>
    private void Check()
    {
        var finish = false;
        lock (_gate)
        {
            if (_done)
            {
                return;
            }
            var tally = Tally.Of(_states.Values);
            if (tally.Settled >= _states.Count)
            {
                finish = true;
            }
            else if (_completeRead && _everyRelay && tally.Complete + (_states.Count - tally.Settled) < _needed)
            {
                // On every relay, no wait once a majority can no longer read completely.
                finish = true;
            }
            else if ((_completeRead ? tally.Complete : tally.Answered) >= _needed)
            {
                // On every relay, the others are waited for one timeout more at most (SPEC.md §13).
                if (!_everyRelay)
                {
                    finish = true;
                }
                else
                {
                    _grace ??= new Timer(_ => Finish(), null, _graceMs, Timeout.Infinite);
                }
            }
        }
        if (finish)
        {
            Finish();
        }
    }

    /// <summary>
    /// Goes on, once: hands over the events of each relay whose read ended (it answered, complete or
    /// not), and on every relay the pages each relay still reading had sent, as events of no answer
    /// (it has not answered, whatever it sent); nothing of a relay that failed. The relays still
    /// reading are dropped.
    /// </summary>
    public void Finish()
    {
        RelayAnswers final;
        lock (_gate)
        {
            if (_done)
            {
                return;
            }
            _done = true;
            _grace?.Dispose();
            var answers = _states
                .Where(state => state.Value is RelayState.Complete or RelayState.Incomplete)
                .Select(state => KeyValuePair.Create(state.Key, _events[state.Key]));
            var partial = new Dictionary<string, IReadOnlyList<object>>(StringComparer.Ordinal);
            if (_everyRelay)
            {
                foreach (var (relay, events) in _pages)
                {
                    lock (events)
                    {
                        if (_states[relay] == RelayState.Reading && events.Count > 0)
                        {
                            partial[relay] = [.. events];
                        }
                    }
                }
            }
            if (_completeRead)
            {
                var complete = Tally.Of(_states.Values).Complete;
                final = new CompleteAnswers(new Dictionary<string, IReadOnlyList<object>>(answers, StringComparer.Ordinal), partial, complete);
            }
            else
            {
                final = new RelayAnswers(new Dictionary<string, IReadOnlyList<object>>(answers, StringComparer.Ordinal), partial);
            }
        }
        _stop.Cancel();
        _result.TrySetResult(final);
    }
}

/// <summary>
/// The relays of one call (SPEC.md §13): one connection per relay, opened at its first request,
/// kept for the call's next requests, closed by <see cref="Close"/> or <see cref="Release"/>.
/// Every relay of a list at once; a call goes on as soon as a majority of them answered (any two
/// majorities of one list share a relay), but the reads on every relay. Subscription ids are random:
/// what a relay receives names nothing of the integration. Each request (a page of a read, a
/// publication), the wait for the connection included, is bounded by the relay timeout.
/// </summary>
internal sealed class RelayPool
{
    /// <summary>NIP-42 prefix of a refusal that authentication may lift.</summary>
    private const string AuthRequired = "auth-required:";

    private readonly IWebSocketFactory _factory;
    private readonly int _timeoutMs;
    private readonly CancellationToken _cancellationToken;
    private readonly RandomSource _random;
    private readonly Func<long> _now;
    private readonly object _gate = new();
    private readonly Dictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _pending = [];

    /// <summary>
    /// Relays a request to which ran out of time or lost its connection in this call: a read on
    /// every relay counts them as failed at once (SPEC.md §13).
    /// </summary>
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);
    private bool _closed;

    public RelayPool(IWebSocketFactory factory, int timeoutMs, RandomSource random, Func<long> now, CancellationToken cancellationToken)
    {
        _factory = factory;
        _timeoutMs = timeoutMs;
        _cancellationToken = cancellationToken;
        _random = random;
        _now = now;
    }

    /// <summary>More than half of <paramref name="count"/> relays: any two majorities of one list share a relay.</summary>
    public static int Majority(int count) => (count / 2) + 1;

    /// <summary>
    /// What every relay of <paramref name="relays"/> holds that matches <paramref name="filter"/>, in
    /// parallel (page after page when <paramref name="paged"/>). Completes as soon as
    /// <paramref name="needed"/> relays answered (the others' reads are dropped), or once every relay
    /// answered or failed: what <paramref name="keep"/> kept of each relay that answered. On every
    /// relay (<paramref name="everyRelay"/>), it waits for the others one timeout more at most once a
    /// majority answered, counts relays that failed earlier in the call as failed at once, and keeps
    /// the pages a relay still reading had sent by then, as events of no answer. Never fails.
    /// </summary>
    public Task<RelayAnswers> QueryRelaysAsync(
        IReadOnlyList<string> relays,
        RelayFilter filter,
        byte[] authKey,
        int needed,
        bool paged,
        Func<Json, object?> keep,
        bool everyRelay = false)
    {
        var read = new ListRead(relays, everyRelay ? IsFailed : static _ => false, needed, everyRelay, completeRead: false, _timeoutMs);
        foreach (var relay in read.Asked)
        {
            _ = ReadAsync(relay);
        }
        return read.Result;

        async Task ReadAsync(string relay)
        {
            IReadOnlyList<object> events;
            try
            {
                events = paged
                    ? await PagesAsync(relay, filter, authKey, keep, read.Pages(relay), read.Stop).ConfigureAwait(false)
                    : KeptOf(await QueryAsync(relay, filter, authKey, keep, read.Stop).ConfigureAwait(false));
            }
#pragma warning disable CA1031 // A relay that fails is one that did not answer.
            catch (Exception)
#pragma warning restore CA1031
            {
                read.Failed(relay);
                return;
            }
            // Of a read that is not a complete read, any read that ended is complete.
            read.Ended(relay, events, complete: true);
        }
    }

    /// <summary>
    /// A complete read of <paramref name="filter"/> on every relay of <paramref name="relays"/> at
    /// once (SPEC.md §13, "Complete reads"): pages of 100 events, the next one from the oldest second
    /// of the last, until a page is not full. Completes as soon as <paramref name="needed"/> relays
    /// read completely (the others' reads are dropped), or once every relay answered, stopped
    /// incomplete or failed; on every relay, as <see cref="QueryRelaysAsync"/> does, and also as soon
    /// as a majority can no longer read completely. Never fails.
    /// </summary>
    public async Task<CompleteAnswers> QueryCompleteAsync(
        IReadOnlyList<string> relays,
        RelayFilter filter,
        byte[] authKey,
        int needed,
        Func<Json, object?> keep,
        bool everyRelay = false)
    {
        var read = new ListRead(relays, everyRelay ? IsFailed : static _ => false, needed, everyRelay, completeRead: true, _timeoutMs);
        foreach (var relay in read.Asked)
        {
            _ = ReadAsync(relay);
        }
        return (CompleteAnswers)await read.Result.ConfigureAwait(false);

        async Task ReadAsync(string relay)
        {
            (IReadOnlyList<object> Events, bool Complete) pages;
            try
            {
                pages = await CompletePagesAsync(relay, filter, authKey, keep, read.Pages(relay), read.Stop).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A relay that fails is one that did not answer.
            catch (Exception)
#pragma warning restore CA1031
            {
                read.Failed(relay);
                return;
            }
            read.Ended(relay, pages.Events, pages.Complete);
        }
    }

    /// <summary>
    /// Publishes <paramref name="event"/> to every relay of <paramref name="relays"/>, in parallel.
    /// Completes once <paramref name="quorum"/> relays took it (the others go on until
    /// <see cref="Release"/>, each bounded by its timeout); fails as soon as they no longer can.
    /// </summary>
    public Task PublishToQuorumAsync(IReadOnlyList<string> relays, NostrEvent @event, byte[] authKey, int quorum)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object();
        var taken = 0;
        var failures = 0;
        var running = relays.Count;
        // Cancelled when it fails: only the relays past a majority may finish their own request
        // (SPEC.md §13). Disposed once every relay's request ended.
        var stop = new CancellationTokenSource();

        void Settle()
        {
            var failed = false;
            lock (gate)
            {
                if (taken >= quorum)
                {
                    result.TrySetResult();
                }
                else if (relays.Count - failures < quorum)
                {
                    failed = result.TrySetException(new QuorumException($"Fewer than {quorum} relays took the event"));
                }
            }
            if (failed)
            {
                stop.Cancel();
            }
        }

        Settle();
        if (relays.Count == 0)
        {
            stop.Dispose();
        }
        foreach (var relay in relays)
        {
            _ = PublishOneAsync(relay);
        }
        return result.Task;

        async Task PublishOneAsync(string relay)
        {
            try
            {
                await PublishAsync(relay, @event, authKey, stop.Token).ConfigureAwait(false);
                lock (gate)
                {
                    taken++;
                }
            }
#pragma warning disable CA1031 // A relay that refuses or fails is one that did not take it.
            catch (Exception)
#pragma warning restore CA1031
            {
                lock (gate)
                {
                    failures++;
                }
            }
            Settle();
            if (Interlocked.Decrement(ref running) == 0)
            {
                stop.Dispose();
            }
        }
    }

    /// <summary>Closes every connection now: their requests fail.</summary>
    public void Close()
    {
        Connection[] connections;
        lock (_gate)
        {
            _closed = true;
            connections = [.. _connections.Values];
            _connections.Clear();
        }
        foreach (var connection in connections)
        {
            connection.Close();
        }
    }

    /// <summary>
    /// Closes every connection once the requests still running are over (a write to the relays past
    /// the quorum), each bounded by its timeout. Nothing new may be asked of the pool.
    /// </summary>
    public void Release()
    {
        Task[] pending;
        lock (_gate)
        {
            _closed = true;
            pending = [.. _pending];
        }
        if (pending.Length == 0)
        {
            Close();
            return;
        }
        _ = Task.WhenAll(pending).ContinueWith(
            all =>
            {
                _ = all.Exception;
                Close();
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private bool IsFailed(string relay)
    {
        lock (_gate)
        {
            return _failed.Contains(relay);
        }
    }

    private static List<object> KeptOf(IReadOnlyList<Entry> entries) =>
        entries.Where(entry => entry.Kept is not null).Select(entry => entry.Kept!).ToList();

    /// <summary>
    /// One page of a complete read (SPEC.md §13, "Complete reads"), read: what the read asks next
    /// (the page's <c>until</c>), or how it ends. <paramref name="seen"/> holds the ids of the read so
    /// far; the page's new ones are added to it, and what was kept of them to <paramref name="kept"/>.
    /// </summary>
    public static (long? Until, bool Complete) AfterCompletePage(IReadOnlyList<Entry> answer, HashSet<string> seen, List<object> kept, int pagesRead)
    {
        long? oldest = null;
        var fresh = false;
        foreach (var entry in answer)
        {
            if (entry.Id is null)
            {
                continue;
            }
            if (entry.CreatedAt is { } createdAt)
            {
                oldest = oldest is null ? createdAt : Math.Min(oldest.Value, createdAt);
            }
            if (!seen.Add(entry.Id))
            {
                continue;
            }
            if (entry.Kept is not null)
            {
                lock (kept)
                {
                    kept.Add(entry.Kept);
                }
            }
            // New, dated or not (SPEC.md §13: "no event not seen before"): the next page may hold more.
            fresh = true;
        }
        // More than asked: the relay ignored the limit, and what it left out cannot be told.
        if (answer.Count > Protocol.CompleteReadLimit)
        {
            return (null, false);
        }
        if (answer.Count < Protocol.CompleteReadLimit)
        {
            return (null, true);
        }
        // Full: the next page from its oldest second, unless it cannot move on.
        if (oldest is null || !fresh || pagesRead >= Protocol.MaxPages)
        {
            return (null, false);
        }
        return (oldest, false);
    }

    /// <summary>
    /// One page of a paged read (SPEC.md §13, "Paged reads"), read: the <c>until</c> of the next page,
    /// or null when the read ends there. Only an event's string id and safe integer date page the
    /// read, whatever else it holds (one that does not check out pages it all the same); one without
    /// them is not kept. <paramref name="seen"/> holds the ids of the read so far; the page's new ones
    /// are added to it, and what was kept of them to <paramref name="kept"/>. The next page from the
    /// oldest second, or a second earlier when none of the dated events was new.
    /// </summary>
    public static long? AfterPagedPage(IReadOnlyList<Entry> answer, HashSet<string> seen, List<object> kept)
    {
        long? oldest = null;
        var fresh = false;
        foreach (var entry in answer)
        {
            if (entry.Id is null || entry.CreatedAt is not { } createdAt)
            {
                continue;
            }
            oldest = oldest is null ? createdAt : Math.Min(oldest.Value, createdAt);
            if (!seen.Add(entry.Id))
            {
                continue;
            }
            if (entry.Kept is not null)
            {
                lock (kept)
                {
                    kept.Add(entry.Kept);
                }
            }
            fresh = true;
        }
        return oldest is null ? null : fresh ? oldest : oldest - 1;
    }

    /// <summary>The stored events of <paramref name="url"/> that match <paramref name="filter"/>, page after page.</summary>
    private async Task<IReadOnlyList<object>> PagesAsync(string url, RelayFilter filter, byte[] authKey, Func<Json, object?> keep, List<object> events, CancellationToken stop)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var until = filter.Until;
        for (var page = 0; page < Protocol.MaxPages; page++)
        {
            IReadOnlyList<Entry> answer;
            try
            {
                answer = await QueryAsync(url, until is null ? filter : filter with { Until = until }, authKey, keep, stop).ConfigureAwait(false);
            }
            catch (Exception) when (page > 0 && !stop.IsCancellationRequested)
            {
                // A relay that stops answering after its first page keeps what it sent.
                break;
            }
            if (AfterPagedPage(answer, seen, events) is not { } next)
            {
                break;
            }
            until = next;
        }
        lock (events)
        {
            return [.. events];
        }
    }

    /// <summary>
    /// The events of <paramref name="url"/> that match <paramref name="filter"/>, read completely if
    /// it can be: whether it was. Fails when its first page fails (the relay failed); a later failure
    /// ends the read incomplete.
    /// </summary>
    private async Task<(IReadOnlyList<object> Events, bool Complete)> CompletePagesAsync(
        string url, RelayFilter filter, byte[] authKey, Func<Json, object?> keep, List<object> events, CancellationToken stop)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var until = filter.Until;
        for (var page = 1; ; page++)
        {
            IReadOnlyList<Entry> answer;
            try
            {
                answer = await QueryAsync(url, filter with { Limit = Protocol.CompleteReadLimit, Until = until }, authKey, keep, stop).ConfigureAwait(false);
            }
            catch (Exception) when (page > 1 && !stop.IsCancellationRequested)
            {
                return (Snapshot(events), false);
            }
            var next = AfterCompletePage(answer, seen, events, page);
            if (next.Until is null)
            {
                return (Snapshot(events), next.Complete);
            }
            until = next.Until;
        }
    }

    private static List<object> Snapshot(List<object> events)
    {
        lock (events)
        {
            return [.. events];
        }
    }

    /// <summary>
    /// The stored events of one relay that match <paramref name="filter"/>, as it sent them: the
    /// entry of each, unchecked (the caller verifies what it kept). At most 1000. Authenticates when
    /// the relay asks (NIP-42).
    /// </summary>
    private Task<IReadOnlyList<Entry>> QueryAsync(string url, RelayFilter filter, byte[] authKey, Func<Json, object?> keep, CancellationToken stop) =>
        RequestAsync(url, async (connection, scope) =>
        {
            var answer = await AskAsync(connection, scope, filter, keep).ConfigureAwait(false);
            if (answer.Closed is { } closed && closed.StartsWith(AuthRequired, StringComparison.Ordinal)
                && await AuthenticateAsync(connection, scope, authKey).ConfigureAwait(false))
            {
                answer = await AskAsync(connection, scope, filter, keep).ConfigureAwait(false);
            }
            if (answer.Closed is not null)
            {
                throw new RelayException($"{url} closed the query: {answer.Closed}");
            }
            return answer.Events;
        }, stop);

    /// <summary>
    /// One subscription: its events until EOSE (or the 1000th), or its CLOSED reason. Of each id, the
    /// first event is read; another of that id only counts toward the 1000 (SPEC.md §13, "Limits").
    /// Its random id drawn and its REQ sent only while the request runs (<see cref="RequestScope.TryRun"/>):
    /// else it is cancelled, having asked nothing.
    /// </summary>
    private Task<(IReadOnlyList<Entry> Events, string? Closed)> AskAsync(Connection connection, RequestScope scope, RelayFilter filter, Func<Json, object?> keep)
    {
        var subscription = "";
        var events = new List<Entry>();
        // The ids of this query's events so far: the first event of each is the one read.
        var read = new HashSet<string>(StringComparer.Ordinal);
        var done = new TaskCompletionSource<(IReadOnlyList<Entry>, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object();
        var open = true;
        Action? stopListening = null;

        void End(string? closed, bool byRelay)
        {
            lock (gate)
            {
                if (!open)
                {
                    return;
                }
                open = false;
            }
            stopListening?.Invoke();
            // Over this connection, which stays open for the call's next requests.
            if (!byRelay)
            {
                connection.Send(new JsonArray().Add(Json.Of("CLOSE")).Add(Json.Of(subscription)));
            }
            List<Entry> answer;
            lock (gate)
            {
                answer = [.. events];
            }
            done.TrySetResult((answer, closed));
        }

        void Listen() => stopListening = connection.Listen(message =>
        {
            if (message.Count < 2 || JsonRead.String(message[1]) != subscription)
            {
                return;
            }
            switch (JsonRead.String(message[0]))
            {
                case "EVENT":
                    var full = false;
                    lock (gate)
                    {
                        if (!open)
                        {
                            return;
                        }
                        events.Add(Entry.Of(message.Count > 2 ? message[2] : null, keep, read));
                        full = events.Count >= Protocol.MaxEventsPerQuery;
                    }
                    if (full)
                    {
                        End(null, byRelay: false);
                    }
                    break;
                case "EOSE":
                    End(null, byRelay: false);
                    break;
                case "CLOSED":
                    End(message.Count > 2 && JsonRead.String(message[2]) is { } reason ? reason : "", byRelay: true);
                    break;
            }
        });
        var asked = scope.TryRun(() =>
        {
            subscription = Text.Hex(_random.Bytes(8));
            Listen();
            scope.OnEnd(() =>
            {
                lock (gate)
                {
                    if (!open)
                    {
                        return;
                    }
                    open = false;
                }
                stopListening?.Invoke();
                connection.Send(new JsonArray().Add(Json.Of("CLOSE")).Add(Json.Of(subscription)));
                done.TrySetCanceled();
            });
            connection.Send(new JsonArray().Add(Json.Of("REQ")).Add(Json.Of(subscription)).Add(filter.ToJson()));
        });
        return asked ? done.Task : Task.FromCanceled<(IReadOnlyList<Entry>, string?)>(new CancellationToken(canceled: true));
    }

    /// <summary>
    /// Authenticates over <paramref name="connection"/> with <paramref name="authKey"/> (NIP-42) for a
    /// request, while it runs: once it is over (its relay dropped by a read that went on, its time
    /// run out), no authentication is signed nor sent, and no random byte is drawn for one (SPEC.md
    /// §13). False then, as for an authentication the relay refused.
    /// </summary>
    private Task<bool> AuthenticateAsync(Connection connection, RequestScope scope, byte[] authKey)
    {
        if (scope.Ended)
        {
            return Task.FromResult(false);
        }
        var createdAt = _now();
        Task<bool>? authenticated = null;
        return scope.TryRun(() => authenticated = connection.AuthenticateAsync(authKey, createdAt, _random))
            ? authenticated!
            : Task.FromResult(false);
    }

    /// <summary>Publishes <paramref name="event"/> to one relay, authenticating when it asks (NIP-42). Fails unless taken.</summary>
    private Task<bool> PublishAsync(string url, NostrEvent @event, byte[] authKey, CancellationToken stop) =>
        RequestAsync(url, async (connection, scope) =>
        {
            var answer = await WriteAsync(connection, scope, @event).ConfigureAwait(false);
            if (!answer.Accepted && answer.Reason.StartsWith(AuthRequired, StringComparison.Ordinal)
                && await AuthenticateAsync(connection, scope, authKey).ConfigureAwait(false))
            {
                answer = await WriteAsync(connection, scope, @event).ConfigureAwait(false);
            }
            if (!answer.Accepted)
            {
                throw new RelayRefusalException(url, answer.Reason);
            }
            return true;
        }, stop);

    /// <summary>One write of <paramref name="event"/>: the relay's OK. Sent only while the request runs; else cancelled, having sent nothing.</summary>
    private static Task<(bool Accepted, string Reason)> WriteAsync(Connection connection, RequestScope scope, NostrEvent @event)
    {
        var done = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action? stop = null;
        void Listen() => stop = connection.Listen(message =>
        {
            if (message.Count < 2 || JsonRead.String(message[0]) != "OK" || JsonRead.String(message[1]) != @event.Id)
            {
                return;
            }
            stop?.Invoke();
            done.TrySetResult((
                message.Count > 2 && JsonRead.IsTrue(message[2]),
                message.Count > 3 && JsonRead.String(message[3]) is { } reason ? reason : ""));
        });
        var sent = scope.TryRun(() =>
        {
            Listen();
            scope.OnEnd(() =>
            {
                stop?.Invoke();
                done.TrySetCanceled();
            });
            connection.Send(new JsonArray().Add(Json.Of("EVENT")).Add(@event.ToJson()));
        });
        return sent ? done.Task : Task.FromCanceled<(bool, string)>(new CancellationToken(canceled: true));
    }

    /// <summary>
    /// Runs <paramref name="operation"/> over the connection to <paramref name="url"/> once it is
    /// open, within the timeout. Fails when the connection ends or the time runs out (the relay then
    /// counts as failed for the reads on every relay of this call), or when the call or
    /// <paramref name="stop"/> is cancelled. What the operation leaves behind (listeners,
    /// subscriptions) is cleaned up when it settles.
    /// </summary>
    private Task<T> RequestAsync<T>(string url, Func<Connection, RequestScope, Task<T>> operation, CancellationToken stop)
    {
        var scope = new RequestScope();
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Settle(Exception? error, T? value)
        {
            if (!scope.End())
            {
                return;
            }
            if (error is null)
            {
                result.TrySetResult(value!);
            }
            else
            {
                result.TrySetException(error);
            }
        }

        void Fail(Exception error)
        {
            if (scope.Ended)
            {
                return;
            }
            lock (_gate)
            {
                _failed.Add(url);
            }
            Settle(error, default);
        }

        lock (_gate)
        {
            _pending.Add(result.Task);
        }
        _ = result.Task.ContinueWith(
            task =>
            {
                _ = task.Exception;
                lock (_gate)
                {
                    _pending.Remove(task);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var timer = new Timer(_ => Fail(new RelayException($"{url}: no answer in time")), null, _timeoutMs, Timeout.Infinite);
        scope.OnEnd(() => timer.Dispose());
        foreach (var token in new[] { _cancellationToken, stop })
        {
            if (!token.CanBeCanceled)
            {
                continue;
            }
            var registration = token.Register(() => Settle(new OperationCanceledException(token), default));
            scope.OnEnd(() => registration.Dispose());
        }
        if (scope.Ended)
        {
            return result.Task;
        }
        Connection connection;
        try
        {
            connection = GetConnection(url);
        }
#pragma warning disable CA1031 // Anything that stops the connection fails the relay.
        catch (Exception error)
#pragma warning restore CA1031
        {
            Fail(error);
            return result.Task;
        }
        scope.OnEnd(connection.OnDeath(Fail));
        _ = RunAsync();
        return result.Task;

        async Task RunAsync()
        {
            try
            {
                await connection.Opened.ConfigureAwait(false);
                if (scope.Ended)
                {
                    return;
                }
                var value = await operation(connection, scope).ConfigureAwait(false);
                Settle(null, value);
            }
#pragma warning disable CA1031 // The operation's failure is the request's.
            catch (Exception error)
#pragma warning restore CA1031
            {
                Settle(error, default);
            }
        }
    }

    /// <summary>The pool's connection to <paramref name="url"/>, opened at its first request.</summary>
    private Connection GetConnection(string url)
    {
        lock (_gate)
        {
            if (_closed)
            {
                throw new RelayException($"{url}: the pool is closed");
            }
            if (!_connections.TryGetValue(url, out var connection))
            {
                connection = new Connection(url, _factory, _timeoutMs);
                _connections[url] = connection;
            }
            return connection;
        }
    }

    /// <summary>What a request registers to clean up once it settles; ended once only.</summary>
    private sealed class RequestScope
    {
        private readonly object _gate = new();
        private readonly List<Action> _cleanups = [];
        private bool _ended;

        public bool Ended
        {
            get
            {
                lock (_gate)
                {
                    return _ended;
                }
            }
        }

        /// <summary>
        /// Runs <paramref name="start"/> while the request runs, which ends only once it ran: whether
        /// it ran. What a request starts (a subscription and its random id, an authentication, a
        /// write) goes through it, so that a request over, the relay of a read that went on among
        /// them, starts nothing more (SPEC.md §13). <paramref name="start"/> neither waits nor ends
        /// the request.
        /// </summary>
        public bool TryRun(Action start)
        {
            lock (_gate)
            {
                if (_ended)
                {
                    return false;
                }
                start();
                return true;
            }
        }

        /// <summary>Registers <paramref name="cleanup"/>, or runs it now when the request is over.</summary>
        public void OnEnd(Action cleanup)
        {
            lock (_gate)
            {
                if (!_ended)
                {
                    _cleanups.Add(cleanup);
                    return;
                }
            }
            cleanup();
        }

        /// <summary>Ends the request: true for the first call, which runs the cleanups.</summary>
        public bool End()
        {
            Action[] cleanups;
            lock (_gate)
            {
                if (_ended)
                {
                    return false;
                }
                _ended = true;
                cleanups = [.. _cleanups];
                _cleanups.Clear();
            }
            foreach (var cleanup in cleanups)
            {
                cleanup();
            }
            return true;
        }
    }
}
