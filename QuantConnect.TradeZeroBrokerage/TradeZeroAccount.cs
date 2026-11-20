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
using System.Globalization;
using QuantConnect.Brokerages.TradeZero.Api;

namespace QuantConnect.Brokerages.TradeZero
{
    /// <summary>
    /// Read-only snapshot of the TradeZero account's margin and P&amp;L figures, as reported by the broker.
    /// Refreshed by the brokerage's polling loop (GET /pnl every few seconds, GET /account less often).
    /// </summary>
    /// <remarks>
    /// Lean's own view of margin is available as usual (Portfolio.MarginRemaining, TotalMarginUsed,
    /// GetBuyingPower). This object exposes the broker's numbers, which decide whether TradeZero accepts an order.
    ///
    /// Python:
    /// <code>
    /// from AlgorithmImports import *
    /// from clr import AddReference
    /// AddReference("QuantConnect.Brokerages.TradeZero")
    /// from QuantConnect.Brokerages.TradeZero import TradeZeroAccount
    ///
    /// account = TradeZeroAccount.get_current()    # None until the brokerage connected (and in backtests)
    /// if account is not None:
    ///     self.debug(f"BP={account.buying_power} overnight BP={account.overnight_buying_power} "
    ///                f"leverage used={account.used_leverage}/{account.allowed_leverage}")
    /// </code>
    /// Each refresh replaces the object, so keep the reference only as long as you need consistent values.
    /// </remarks>
    public sealed class TradeZeroAccount
    {
        private static TradeZeroAccount _current;

        /// <summary>
        /// Latest snapshot of the connected TradeZero account, null when no TradeZero brokerage is connected
        /// </summary>
        /// <remarks>In Python this static property is exposed as TradeZeroAccount.CURRENT; prefer <see cref="GetCurrent"/></remarks>
        public static TradeZeroAccount Current => _current;

        /// <summary>
        /// Latest snapshot of the connected TradeZero account, null when no TradeZero brokerage is connected.
        /// Python: TradeZeroAccount.get_current()
        /// </summary>
        public static TradeZeroAccount GetCurrent() => _current;

        /// <summary>TradeZero account number</summary>
        public string AccountId { get; private set; }

        /// <summary>"Paper" for paper accounts</summary>
        public string AccountType { get; private set; }

        /// <summary>"M" margin, "C" cash (as reported)</summary>
        public string MarginType { get; private set; }

        /// <summary>True for a paper account</summary>
        public bool IsPaper => string.Equals(AccountType, TzValues.Paper, StringComparison.OrdinalIgnoreCase);

        // ---- from GET /account/{id} (buying power; some fields are start-of-day snapshots)

        /// <summary>Current buying power (bp)</summary>
        public decimal BuyingPower { get; private set; }

        /// <summary>Buying power available for positions held overnight (start-of-day snapshot)</summary>
        public decimal OvernightBuyingPower { get; private set; }

        /// <summary>Margin requirement of the open positions</summary>
        public decimal MarginRequirement { get; private set; }

        /// <summary>Configured account leverage</summary>
        public decimal Leverage { get; private set; }

        public decimal MarginDeficit { get; private set; }

        public decimal MaintenanceDeficit { get; private set; }

        /// <summary>Start-of-day equity</summary>
        public decimal StartOfDayEquity { get; private set; }

        public decimal TotalCommissions { get; private set; }

        public bool TradingSuspended { get; private set; }

        /// <summary>When the /account figures were read (UTC)</summary>
        public DateTime AccountUpdatedUtc { get; private set; }

        // ---- from GET /accounts/{id}/pnl (live intraday figures)

        /// <summary>Live account value (equity)</summary>
        public decimal AccountValue { get; private set; }

        /// <summary>Cash not used by positions, as TradeZero computes it (short proceeds are not credited)</summary>
        public decimal AvailableCash { get; private set; }

        public decimal AllowedLeverage { get; private set; }

        public decimal UsedLeverage { get; private set; }

        /// <summary>Gross market value of the positions</summary>
        public decimal Exposure { get; private set; }

        public decimal InitialMargin { get; private set; }

        public decimal IntradayMargin { get; private set; }

        public decimal MaintenanceMargin { get; private set; }

        public decimal MarginExcess { get; private set; }

        public decimal EquityRatio { get; private set; }

        public decimal DayPnl { get; private set; }

        public decimal DayRealized { get; private set; }

        public decimal DayUnrealized { get; private set; }

        public decimal TotalUnrealized { get; private set; }

        /// <summary>When the P&amp;L figures were read (UTC)</summary>
        public DateTime PnlUpdatedUtc { get; private set; }

        private TradeZeroAccount()
        {
        }

        /// <summary>
        /// A copy of this snapshot with the /account figures replaced
        /// </summary>
        internal TradeZeroAccount WithAccount(TzAccount account, DateTime utcNow)
        {
            var copy = (TradeZeroAccount)MemberwiseClone();
            copy.AccountId = account.Account;
            copy.AccountType = account.AccountType;
            copy.MarginType = account.MarginType;
            copy.BuyingPower = account.EffectiveBuyingPower;
            copy.OvernightBuyingPower = account.OvernightBp;
            copy.MarginRequirement = account.MarginRequirement;
            copy.Leverage = account.Leverage;
            copy.MarginDeficit = account.MarginDeficit;
            copy.MaintenanceDeficit = account.MaintenanceDeficit;
            copy.StartOfDayEquity = account.SodEquity;
            copy.TotalCommissions = account.TotalCommissions;
            copy.TradingSuspended = account.TradingSuspended;
            copy.AccountUpdatedUtc = utcNow;
            return copy;
        }

        /// <summary>
        /// A copy of this snapshot with the P&amp;L figures replaced
        /// </summary>
        internal TradeZeroAccount WithPnl(TzPnl pnl, DateTime utcNow)
        {
            var copy = (TradeZeroAccount)MemberwiseClone();
            copy.AccountValue = pnl.AccountValue;
            copy.AvailableCash = pnl.AvailableCash;
            copy.AllowedLeverage = pnl.AllowedLeverage;
            copy.UsedLeverage = pnl.UsedLeverage;
            copy.Exposure = pnl.Exposure;
            copy.InitialMargin = pnl.InitialMargin;
            copy.IntradayMargin = pnl.IntradayMargin;
            copy.MaintenanceMargin = pnl.MaintenanceMargin;
            copy.MarginExcess = pnl.MarginExcess;
            copy.EquityRatio = pnl.EquityRatio;
            copy.DayPnl = pnl.DayPnl;
            copy.DayRealized = pnl.DayRealized;
            copy.DayUnrealized = pnl.DayUnrealized;
            copy.TotalUnrealized = pnl.TotalUnrealized;
            copy.PnlUpdatedUtc = utcNow;
            return copy;
        }

        internal static TradeZeroAccount Empty() => new();

        internal static void Publish(TradeZeroAccount snapshot) => _current = snapshot;

        internal static void Clear(string accountId)
        {
            if (_current != null && _current.AccountId == accountId)
            {
                _current = null;
            }
        }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "TradeZeroAccount {0} ({1}): value={2} bp={3} overnightBp={4} cash={5} exposure={6} leverage={7}/{8} marginReq={9} dayPnl={10}",
                AccountId, AccountType, AccountValue, BuyingPower, OvernightBuyingPower, AvailableCash, Exposure,
                UsedLeverage, AllowedLeverage, MarginRequirement, DayPnl);
        }
    }
}
