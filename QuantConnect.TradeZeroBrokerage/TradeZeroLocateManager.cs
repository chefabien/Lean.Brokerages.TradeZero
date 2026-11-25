/*
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using QuantConnect.Brokerages.TradeZero.Api;
using QuantConnect.Brokerages.TradeZero.Logging;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.TradeZero
{
    /// <summary>What a locate workflow ended with</summary>
    internal enum LocateOutcomeKind
    {
        /// <summary>The chosen offer reached 50 (Filled)</summary>
        Filled,
        /// <summary>The handler returned null, threw, or returned a foreign object</summary>
        Declined,
        /// <summary>TradeZero refused every row (54 with an error, 56)</summary>
        Refused,
        /// <summary>TradeZero answered R56: the symbol is now easy to borrow</summary>
        EasyToBorrow,
        /// <summary>The accepted offer expired, or was not filled in time</summary>
        Expired,
        /// <summary>No settled answer to the quote in time</summary>
        TimedOut,
        /// <summary>The order was canceled while the quote was open</summary>
        Canceled
    }

    internal sealed record LocateRequest(string Ticker, Symbol Symbol, int? LeanOrderId, decimal OrderQuantity, long ShortQuantity,
        long AlreadyLocated, long RequestedQuantity);

    internal sealed record LocateOutcome(LocateOutcomeKind Kind, string Message, TradeZeroLocateQuote Quote = null,
        TradeZeroLocateOffer Accepted = null);

    /// <summary>
    /// Runs the TradeZero locate workflows (quote → offers → handler → accept, and sell-backs) for the brokerage and for
    /// <see cref="TradeZeroLocates"/>. Every action on a symbol runs under that symbol's semaphore (<see cref="SymbolLock"/>),
    /// taken by the caller, so two orders never count the same located shares.
    /// </summary>
    internal sealed class TradeZeroLocateManager
    {
        private readonly TradeZeroRestClient _client;
        private readonly Func<string> _accountId;
        private readonly TradeZeroSettings _settings;
        private readonly TradeZeroApiLogger _apiLogger;
        private readonly Action<BrokerageMessageType, string, string> _message;
        private readonly string _runTag;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
        private int _sequence;

        public TradeZeroLocateManager(TradeZeroRestClient client, Func<string> accountId, TradeZeroSettings settings, TradeZeroApiLogger apiLogger,
            Action<BrokerageMessageType, string, string> message, string runTag)
        {
            _client = client;
            _accountId = accountId;
            _settings = settings;
            _apiLogger = apiLogger;
            _message = message;
            _runTag = runTag;
        }

        private string Account => _accountId() ?? throw new InvalidOperationException("TradeZero brokerage is not connected.");

        /// <summary>The semaphore serializing locate actions and HTB short submissions on one symbol</summary>
        public SemaphoreSlim SymbolLock(string ticker) => _locks.GetOrAdd(ticker, _ => new SemaphoreSlim(1, 1));

        /// <summary>Fresh inventory, every symbol and type</summary>
        public List<TzLocateInventoryRow> ReadInventory() => _client.GetLocateInventory(Account);

        /// <summary>Fresh history: today's quotes and sell-backs</summary>
        public List<TzLocateHistoryRow> ReadHistory() => _client.GetLocateHistory(Account);

        /// <summary>Located shares available to short now, summed over types (or for one type)</summary>
        public long Available(string ticker, TradeZeroLocateType? type = null)
        {
            return ReadInventory()
                .Where(r => string.Equals(r.Symbol, ticker, StringComparison.OrdinalIgnoreCase))
                .Where(r => type == null || TzLocateType.FromCode(r.LocateType) == type)
                .Sum(r => Math.Max(0, r.Available));
        }

        /// <summary>A locate id unique across restarts: TradeZero refuses a reused quoteReqID (R114)</summary>
        public string NextId(string prefix) => $"{prefix}{_runTag}.{Interlocked.Increment(ref _sequence)}";

        /// <summary>Rounds a share count up to TradeZero's lot of 100 (quotes must be multiples of 100)</summary>
        public static long RoundUpToLot(long shares) => shares <= 0 ? 0 : (shares + 99) / 100 * 100;

        /// <summary>
        /// Quote, wait for the offers, ask the handler, accept, wait for the fill. The caller holds the symbol's lock.
        /// Blocks for up to tradezero-locate-timeout-seconds, plus the time spent in the handler.
        /// </summary>
        /// <param name="token">Canceled when the Lean order is canceled: open offers are canceled and the workflow stops
        /// (except once an accept was sent: then it waits for the outcome, so the caller can report an unused locate)</param>
        public LocateOutcome Acquire(LocateRequest request, Func<TradeZeroLocateQuote, TradeZeroLocateOffer> handler, CancellationToken token)
        {
            var account = Account;
            var budget = _settings.LocateTimeout;
            var watch = Stopwatch.StartNew();
            var qid = NextId("LQ");
            _apiLogger.LogEvent("lean", "locate quote", new
            {
                quoteReqId = qid, symbol = request.Ticker, leanOrderId = request.LeanOrderId, request.ShortQuantity,
                alreadyLocated = request.AlreadyLocated, requested = request.RequestedQuantity
            });

            try
            {
                _client.RequestLocateQuote(account, request.Ticker, request.RequestedQuantity, qid);
            }
            catch (TradeZeroApiException e) when (!e.IsOutcomeUnknown)
            {
                return new LocateOutcome(LocateOutcomeKind.Refused, $"TradeZero refused the locate quote: {e.ServerMessage}");
            }
            catch (TradeZeroApiException e)
            {
                // the quote may exist: look for its rows anyway
                _apiLogger.LogEvent("lean", "locate quote outcome unknown", new { quoteReqId = qid, error = e.Message });
            }

            // 1. wait until every row has settled, plus one poll so a ".SU" row that appears a little later is not missed
            List<TzLocateHistoryRow> rows = new();
            var settledOnce = false;
            while (true)
            {
                if (token.IsCancellationRequested)
                {
                    CancelOffered(account, rows);
                    return new LocateOutcome(LocateOutcomeKind.Canceled, "order canceled while waiting for the locate quote");
                }
                if (watch.Elapsed > budget)
                {
                    CancelOffered(account, rows);
                    return new LocateOutcome(LocateOutcomeKind.TimedOut,
                        $"no answer to locate quote {qid} within {budget.TotalSeconds:0.#}s");
                }
                rows = ReadQuoteRows(account, qid) ?? rows;
                var settled = rows.Count > 0 && rows.All(r => r.IsSettled);
                var pairComplete = rows.Count >= 2;
                if (settled && (settledOnce || pairComplete))
                {
                    break;
                }
                settledOnce = settled;
                token.WaitHandle.WaitOne(_settings.LocatePollInterval);
            }

            // 2. offers and refusals
            var now = DateTime.UtcNow;
            var offers = rows
                .Where(r => r.LocateStatus == TzLocateStatus.Offered && TzLocateType.FromCode(r.LocateType) != TradeZeroLocateType.Unknown)
                .Select(r => new TradeZeroLocateOffer(TzLocateType.FromCode(r.LocateType), r.QuoteReqId, r.LocateShares,
                    request.ShortQuantity == 0 || request.AlreadyLocated + r.LocateShares >= request.ShortQuantity, r.LocatePrice,
                    r.UpdatedDate?.ToUniversalTime() ?? now))
                .OrderBy(o => o.PricePerShare)
                .ThenBy(o => o.ReusableAfterCover ? 0 : 1)
                .ToList();
            var refused = rows.Where(r => offers.All(o => o.QuoteReqId != r.QuoteReqId)).ToList();
            foreach (var row in refused)
            {
                var type = TzLocateType.FromCode(row.LocateType);
                _apiLogger.LogEvent("lean", "locate refused", new { quoteReqId = row.QuoteReqId, type = type.ToString(), status = row.LocateStatus, error = row.LocateError, text = row.Text });
                _message(BrokerageMessageType.Warning, "LocateRefused",
                    $"{request.Ticker}: locate {row.QuoteReqId}{(type == TradeZeroLocateType.Unknown ? string.Empty : $" ({type})")} refused " +
                    $"(status {row.LocateStatus}, error {row.LocateError}): {row.Text}");
            }
            if (offers.Count == 0)
            {
                var texts = string.Join("; ", refused.Select(r => r.Text).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct());
                if (refused.Any(r => r.LocateError == 56 || (r.Text ?? string.Empty).StartsWith("R56", StringComparison.Ordinal)))
                {
                    return new LocateOutcome(LocateOutcomeKind.EasyToBorrow, texts);
                }
                return new LocateOutcome(LocateOutcomeKind.Refused, string.IsNullOrEmpty(texts) ? "no locate offered" : texts);
            }
            foreach (var offer in offers)
            {
                _apiLogger.LogEvent("lean", "locate offer", new { quoteReqId = offer.QuoteReqId, type = offer.Type.ToString(), offer.Quantity, price = offer.PricePerShare, offer.CoversOrder });
            }
            var quote = new TradeZeroLocateQuote(request.Symbol, request.LeanOrderId, request.OrderQuantity, request.ShortQuantity,
                request.AlreadyLocated, request.RequestedQuantity, qid, offers);

            // 3. the handler (its time does not count against the timeout)
            TradeZeroLocateOffer choice = null;
            string handlerError = null;
            var handlerWatch = Stopwatch.StartNew();
            try
            {
                choice = handler(quote);
            }
            catch (Exception e)
            {
                handlerError = $"the locate handler threw {e.GetType().Name}: {e.Message}";
            }
            handlerWatch.Stop();
            budget += handlerWatch.Elapsed;
            if (choice != null && !offers.Any(o => ReferenceEquals(o, choice)))
            {
                handlerError = "the locate handler returned an object that is not one of quote.Offers";
                choice = null;
            }
            _apiLogger.LogEvent("lean", "locate handler", new { quoteReqId = qid, decision = choice?.QuoteReqId ?? "decline", ms = handlerWatch.ElapsedMilliseconds, error = handlerError });
            if (handlerError != null)
            {
                Log.Error($"TradeZeroLocateManager: {request.Ticker} quote {qid}: {handlerError}");
                _message(BrokerageMessageType.Warning, "LocateHandlerError", $"{request.Ticker}: {handlerError}; counted as a decline.");
            }
            if (token.IsCancellationRequested)
            {
                CancelOffered(account, rows);
                return new LocateOutcome(LocateOutcomeKind.Canceled, "order canceled while the locate handler ran", quote);
            }
            if (choice == null)
            {
                CancelOffered(account, rows);
                var offered = string.Join(", ", offers.Select(o => $"{o.Type} {o.Quantity} @ {o.PricePerShare}"));
                return new LocateOutcome(LocateOutcomeKind.Declined, handlerError ?? $"declined by the locate handler ({offered})", quote);
            }

            // 4. accept (re-sending is documented as safe: one retry when the outcome is unknown)
            _apiLogger.LogEvent("lean", "locate accept", new { quoteReqId = choice.QuoteReqId, type = choice.Type.ToString(), choice.Quantity, price = choice.PricePerShare });
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    _client.AcceptLocate(account, choice.QuoteReqId);
                    break;
                }
                catch (TradeZeroApiException e) when (e.IsOutcomeUnknown && attempt < 2)
                {
                    _apiLogger.LogEvent("lean", "locate accept outcome unknown, resending", new { quoteReqId = choice.QuoteReqId, error = e.Message });
                }
                catch (TradeZeroApiException e) when (e.IsOutcomeUnknown)
                {
                    // the history tells whether it went through
                    _apiLogger.LogEvent("lean", "locate accept outcome unknown", new { quoteReqId = choice.QuoteReqId, error = e.Message });
                    break;
                }
                catch (TradeZeroApiException e)
                {
                    CancelOffered(account, rows);
                    return new LocateOutcome(LocateOutcomeKind.Refused, $"TradeZero refused the locate accept: {e.ServerMessage}", quote);
                }
            }

            // 5. wait for the fill (the sibling row of a Reg SHO quote expires by itself)
            while (true)
            {
                var row = ReadQuoteRows(account, qid)?.FirstOrDefault(r => r.QuoteReqId == choice.QuoteReqId);
                if (row?.LocateStatus == TzLocateStatus.Filled)
                {
                    _apiLogger.LogEvent("lean", "locate filled", new { quoteReqId = choice.QuoteReqId, filled = row.FilledShares, price = row.LocatePrice });
                    _message(BrokerageMessageType.Information, "LocatePurchased",
                        $"{request.Ticker}: located {choice.Quantity} {choice.Type} shares at {choice.PricePerShare} = {choice.TotalCost} USD " +
                        $"(quote {choice.QuoteReqId}{(request.LeanOrderId.HasValue ? $", order {request.LeanOrderId}" : string.Empty)}).");
                    return new LocateOutcome(LocateOutcomeKind.Filled, null, quote, choice);
                }
                if (row != null && (row.LocateStatus == TzLocateStatus.Expired || row.LocateStatus == TzLocateStatus.Canceled))
                {
                    return new LocateOutcome(LocateOutcomeKind.Expired, $"locate offer {choice.QuoteReqId} expired before it was filled", quote);
                }
                if (row != null && row.LocateStatus == TzLocateStatus.Rejected)
                {
                    return new LocateOutcome(LocateOutcomeKind.Refused, $"TradeZero rejected the locate accept: {row.Text}", quote);
                }
                if (watch.Elapsed > budget)
                {
                    TryCancel(account, choice.QuoteReqId);
                    return new LocateOutcome(LocateOutcomeKind.Expired,
                        $"locate offer {choice.QuoteReqId} was not filled within {_settings.LocateTimeout.TotalSeconds:0.#}s after the accept (expired)", quote);
                }
                Thread.Sleep(_settings.LocatePollInterval);
            }
        }

        /// <summary>
        /// Sends a sell-back and waits for TradeZero to queue (48) or refuse (56) it. The caller validated the input and holds the lock.
        /// </summary>
        public TradeZeroLocateResult SellBack(string ticker, long quantity, TradeZeroLocateType type)
        {
            var account = Account;
            var id = NextId("LS");
            _apiLogger.LogEvent("lean", "locate sell-back", new { quoteReqId = id, symbol = ticker, quantity, type = type.ToString() });
            try
            {
                _client.SellLocate(account, ticker, quantity, TzLocateType.SellName(type), id);
            }
            catch (TradeZeroApiException e) when (!e.IsOutcomeUnknown)
            {
                return TradeZeroLocateResult.Rejected($"TradeZero refused the sell-back: {e.ServerMessage}", type, id);
            }
            catch (TradeZeroApiException e)
            {
                _apiLogger.LogEvent("lean", "locate sell-back outcome unknown", new { quoteReqId = id, error = e.Message });
            }

            var watch = Stopwatch.StartNew();
            while (watch.Elapsed <= _settings.LocateTimeout)
            {
                var row = ReadQuoteRows(account, id)?.FirstOrDefault(r => r.QuoteReqId == id);
                if (row != null && (row.LocateStatus == TzLocateStatus.New || row.LocateStatus == TzLocateStatus.Filled))
                {
                    var status = row.LocateStatus == TzLocateStatus.Filled ? TradeZeroLocateStatus.Completed : TradeZeroLocateStatus.Pending;
                    _apiLogger.LogEvent("lean", "locate sold back", new { quoteReqId = id, status = row.LocateStatus });
                    _message(BrokerageMessageType.Information, "LocateSoldBack",
                        $"{ticker}: sell-back of {quantity} {type} located shares {(status == TradeZeroLocateStatus.Completed ? "settled" : "queued")} ({id}).");
                    return new TradeZeroLocateResult(status, type, quantity, quoteReqId: id);
                }
                if (row != null && (row.LocateStatus == TzLocateStatus.Rejected || row.LocateStatus == TzLocateStatus.Canceled))
                {
                    return TradeZeroLocateResult.Rejected($"TradeZero refused the sell-back: {row.Text}", type, id);
                }
                Thread.Sleep(_settings.LocatePollInterval);
            }
            return new TradeZeroLocateResult(TradeZeroLocateStatus.TimedOut, type, quantity, quoteReqId: id,
                message: $"no answer to sell-back {id} within {_settings.LocateTimeout.TotalSeconds:0.#}s");
        }

        /// <summary>History rows of one quote (id and id + ".SU"), null when the read failed</summary>
        private List<TzLocateHistoryRow> ReadQuoteRows(string account, string qid)
        {
            try
            {
                return _client.GetLocateHistory(account)
                    .Where(r => r.QuoteReqId == qid || r.QuoteReqId == qid + ".SU")
                    .ToList();
            }
            catch (TradeZeroApiException e)
            {
                Log.Error($"TradeZeroLocateManager: reading the locate history failed: {e.Message}");
                return null;
            }
        }

        private void CancelOffered(string account, IEnumerable<TzLocateHistoryRow> rows)
        {
            foreach (var row in rows.Where(r => r.LocateStatus == TzLocateStatus.Offered || !r.IsSettled))
            {
                TryCancel(account, row.QuoteReqId);
            }
        }

        private void TryCancel(string account, string quoteReqId)
        {
            try
            {
                _apiLogger.LogEvent("lean", "locate cancel", new { quoteReqId });
                _client.CancelLocate(account, quoteReqId);
            }
            catch (TradeZeroApiException e)
            {
                Log.Error($"TradeZeroLocateManager: canceling locate {quoteReqId} failed: {e.Message}");
            }
        }
    }
}
