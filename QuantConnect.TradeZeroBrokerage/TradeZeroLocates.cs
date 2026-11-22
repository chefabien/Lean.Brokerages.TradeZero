/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using QuantConnect.Brokerages.TradeZero.Api;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.TradeZero
{
    /// <summary>
    /// Locates of the connected TradeZero account: read the located shares, sell them back, or buy a locate without an order.
    /// </summary>
    /// <remarks>
    /// Without a connected TradeZero brokerage (backtests, research) the reads return empty values and the actions return a
    /// Rejected result "not connected". Reads always ask TradeZero (no cache). Actions block the calling thread for up to
    /// tradezero-locate-timeout-seconds, and take the same per-symbol lock as hard-to-borrow short orders.
    ///
    /// Python:
    /// <code>
    /// from QuantConnect.Brokerages.TradeZero import TradeZeroLocates, TradeZeroLocateType
    ///
    /// free = TradeZeroLocates.get_available(self.htbx)           # all types; 0 in backtests
    /// for inv in TradeZeroLocates.get_inventory(self.htbx):
    ///     self.debug(f"{inv.type}: avail={inv.available} in use={inv.unavailable} selling={inv.to_be_sold} sold={inv.sold}")
    /// for r in TradeZeroLocates.sell_back_unused(self.htbx):     # e.g. at 15:50 ET
    ///     self.debug(f"{r.type} {r.status} {r.quantity} {r.message}")
    /// TradeZeroLocates.sell_back(self.htbx, 200, TradeZeroLocateType.PRE_BORROW)
    /// </code>
    /// </remarks>
    public static class TradeZeroLocates
    {
        private const string NotConnected = "TradeZero brokerage is not connected.";
        private static TradeZeroBrokerage _brokerage;

        internal static void Register(TradeZeroBrokerage brokerage) => _brokerage = brokerage;

        internal static void Unregister(TradeZeroBrokerage brokerage)
        {
            if (_brokerage == brokerage)
            {
                _brokerage = null;
            }
        }

        /// <summary>True while a TradeZero brokerage is connected (the other members then reach TradeZero)</summary>
        public static bool IsAvailable => _brokerage?.LocateManager != null && _brokerage.IsConnected;

        // ------------------------------------------------------------------ read

        /// <summary>
        /// Located shares available to short now, summed over all types: neither shorted, reserved by a working order, nor
        /// being sold back. With a type: that type's row only. 0 when not connected.
        /// </summary>
        public static long GetAvailable(Symbol symbol, TradeZeroLocateType? type = null)
        {
            return GetInventory(symbol).Where(i => type == null || i.Type == type).Sum(i => i.Available);
        }

        /// <summary>One row per locate type held for the symbol; empty when none (or not connected)</summary>
        public static IReadOnlyList<TradeZeroLocateInventory> GetInventory(Symbol symbol)
        {
            var b = _brokerage;
            if (b == null || symbol == null)
            {
                return Array.Empty<TradeZeroLocateInventory>();
            }
            var ticker = b.TickerOf(symbol);
            return GetInventory().Where(i => string.Equals(b.TickerOf(i.Symbol), ticker, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>Every inventory row: all symbols, all types; empty when not connected</summary>
        public static IReadOnlyList<TradeZeroLocateInventory> GetInventory()
        {
            var b = _brokerage;
            if (b?.LocateManager == null || b.AccountId == null)
            {
                return Array.Empty<TradeZeroLocateInventory>();
            }
            var now = DateTime.UtcNow;
            return b.LocateManager.ReadInventory()
                .Select(r => new TradeZeroLocateInventory(b.LeanSymbolOf(r.Symbol), TzLocateType.FromCode(r.LocateType), r.Available,
                    r.Unavailable, r.ToBeSold, r.Sold, now))
                .ToList();
        }

        /// <summary>Today's locate quotes (including ".SU" rows), accepts and sell-backs; empty when not connected</summary>
        public static IReadOnlyList<TradeZeroLocateRecord> GetHistory()
        {
            var b = _brokerage;
            if (b?.LocateManager == null || b.AccountId == null)
            {
                return Array.Empty<TradeZeroLocateRecord>();
            }
            return b.LocateManager.ReadHistory()
                .Select(r => new TradeZeroLocateRecord(b.LeanSymbolOf(r.Symbol), r.QuoteReqId, TzLocateType.FromCode(r.LocateType), r.LocateStatus,
                    r.LocateShares, r.FilledShares, r.LocatePrice, r.LocateError, r.Text, r.CreatedDate?.ToUniversalTime(), r.UpdatedDate?.ToUniversalTime()))
                .ToList();
        }

        /// <summary>TradeZero's easy-to-borrow answer, cached per ET day; null when unknown or not connected</summary>
        public static bool? IsEasyToBorrow(Symbol symbol)
        {
            var b = _brokerage;
            return b == null || symbol == null ? null : b.EasyToBorrow(b.TickerOf(symbol));
        }

        // ------------------------------------------------------------------ act

        /// <summary>
        /// Sells located shares of one type back for credit. quantity: a multiple of 100, at most GetAvailable(symbol, type).
        /// type may be omitted only when the symbol has a single inventory row. Returns Pending once TradeZero queued it.
        /// </summary>
        public static TradeZeroLocateResult SellBack(Symbol symbol, long quantity, TradeZeroLocateType? type = null)
        {
            return Run(symbol, (b, ticker) => SellBackLocked(b, ticker, symbol, quantity, type));
        }

        /// <summary>
        /// For each type held: sells back floor(available / 100) · 100 shares. One result per type sold (a SingleUse row that
        /// was used comes back Rejected: TradeZero only credits SingleUse that was never used).
        /// </summary>
        public static IReadOnlyList<TradeZeroLocateResult> SellBackUnused(Symbol symbol)
        {
            var results = new List<TradeZeroLocateResult>();
            var single = Run(symbol, (b, ticker) =>
            {
                foreach (var row in InventoryRows(b, ticker).Where(r => r.Available >= 100))
                {
                    var type = TzLocateType.FromCode(row.LocateType);
                    results.Add(b.LocateManager.SellBack(ticker, row.Available / 100 * 100, type));
                }
                return null;
            });
            if (single != null)
            {
                results.Add(single);
            }
            return results;
        }

        /// <summary>
        /// Buys a locate without an order (e.g. before the open, or ahead of an update that needs more shares).
        /// quantity: a multiple of 100. The handler works as <see cref="TradeZeroOrderProperties.LocateHandler"/>.
        /// </summary>
        public static TradeZeroLocateResult Locate(Symbol symbol, long quantity, Func<TradeZeroLocateQuote, TradeZeroLocateOffer> handler)
        {
            if (handler == null)
            {
                return TradeZeroLocateResult.Rejected("a locate handler is required");
            }
            if (quantity < 100 || quantity % 100 != 0)
            {
                return TradeZeroLocateResult.Rejected($"locates are sold in lots of 100 shares (got {quantity})");
            }
            return Run(symbol, (b, ticker) =>
            {
                var held = InventoryRows(b, ticker).Sum(r => Math.Max(0, r.Available));
                var outcome = b.LocateManager.Acquire(new LocateRequest(ticker, symbol, null, 0m, 0, held, quantity), handler, CancellationToken.None);
                var offer = outcome.Accepted;
                return outcome.Kind switch
                {
                    LocateOutcomeKind.Filled => new TradeZeroLocateResult(TradeZeroLocateStatus.Completed, offer.Type, offer.Quantity, offer.PricePerShare, offer.QuoteReqId),
                    LocateOutcomeKind.Declined => new TradeZeroLocateResult(TradeZeroLocateStatus.Declined, quoteReqId: outcome.Quote?.QuoteReqId, message: outcome.Message),
                    LocateOutcomeKind.Expired => new TradeZeroLocateResult(TradeZeroLocateStatus.Expired, quoteReqId: outcome.Quote?.QuoteReqId, message: outcome.Message),
                    LocateOutcomeKind.TimedOut => new TradeZeroLocateResult(TradeZeroLocateStatus.TimedOut, quoteReqId: outcome.Quote?.QuoteReqId, message: outcome.Message),
                    _ => TradeZeroLocateResult.Rejected(outcome.Message, quoteReqId: outcome.Quote?.QuoteReqId)
                };
            });
        }

        private static TradeZeroLocateResult SellBackLocked(TradeZeroBrokerage b, string ticker, Symbol symbol, long quantity, TradeZeroLocateType? type)
        {
            if (quantity < 100 || quantity % 100 != 0)
            {
                return TradeZeroLocateResult.Rejected($"locates are sold back in lots of 100 shares (got {quantity})", type ?? TradeZeroLocateType.Unknown);
            }
            var rows = InventoryRows(b, ticker);
            if (type == null)
            {
                var types = rows.Where(r => r.Available > 0).Select(r => TzLocateType.FromCode(r.LocateType)).Distinct().ToList();
                if (types.Count > 1)
                {
                    return TradeZeroLocateResult.Rejected($"several locate types held for {symbol.Value} ({string.Join(", ", types)}): pass the type to sell back");
                }
                type = types.Count == 1 ? types[0] : rows.Select(r => TzLocateType.FromCode(r.LocateType)).FirstOrDefault();
            }
            var available = rows.Where(r => TzLocateType.FromCode(r.LocateType) == type).Sum(r => Math.Max(0, r.Available));
            if (quantity > available)
            {
                return TradeZeroLocateResult.Rejected($"only {available} {type} located shares of {symbol.Value} are available to sell back (asked {quantity})", type.Value);
            }
            return b.LocateManager.SellBack(ticker, quantity, type.Value);
        }

        private static List<TzLocateInventoryRow> InventoryRows(TradeZeroBrokerage b, string ticker)
        {
            return b.LocateManager.ReadInventory().Where(r => string.Equals(r.Symbol, ticker, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>Runs an action under the symbol's lock, turning "not connected" and errors into Rejected results</summary>
        private static TradeZeroLocateResult Run(Symbol symbol, Func<TradeZeroBrokerage, string, TradeZeroLocateResult> action)
        {
            var b = _brokerage;
            if (b?.LocateManager == null || b.AccountId == null || !b.IsConnected)
            {
                return TradeZeroLocateResult.Rejected(NotConnected);
            }
            if (symbol == null)
            {
                return TradeZeroLocateResult.Rejected("symbol is required");
            }
            string ticker;
            try
            {
                ticker = b.TickerOf(symbol);
            }
            catch (ArgumentException e)
            {
                return TradeZeroLocateResult.Rejected(e.Message);
            }
            var gate = b.LocateManager.SymbolLock(ticker);
            if (!gate.Wait(b.LocateTimeout))
            {
                return new TradeZeroLocateResult(TradeZeroLocateStatus.TimedOut, message: $"{ticker}: another locate action or short order is still running");
            }
            try
            {
                return action(b, ticker);
            }
            catch (Exception e)
            {
                Log.Error(e, $"TradeZeroLocates: {ticker}");
                return TradeZeroLocateResult.Rejected($"TradeZero locate request failed: {e.Message}");
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
