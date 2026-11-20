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
using QuantConnect.Interfaces;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.TradeZero
{
    /// <summary>
    /// Shortable provider backed by TradeZero's easy-to-borrow check. Locates are not supported, so
    /// a hard-to-borrow symbol is reported as not shortable (0) and Lean blocks short orders before they
    /// are sent; easy-to-borrow symbols have no limit (null).
    /// </summary>
    /// <remarks>
    /// While no TradeZero brokerage is connected (backtests, research) every symbol is reported as
    /// shortable, like Lean's default provider. The connected brokerage caches answers per symbol and day.
    /// </remarks>
    public class TradeZeroShortableProvider : IShortableProvider
    {
        private static Func<string, bool?> _easyToBorrowResolver;

        /// <summary>
        /// Shared instance
        /// </summary>
        public static TradeZeroShortableProvider Instance { get; } = new();

        /// <summary>
        /// Set by the connected brokerage: ticker -> easy to borrow (null when unknown)
        /// </summary>
        internal static void SetResolver(Func<string, bool?> resolver) => _easyToBorrowResolver = resolver;

        internal static void ClearResolver(Func<string, bool?> resolver)
        {
            if (_easyToBorrowResolver == resolver)
            {
                _easyToBorrowResolver = null;
            }
        }

        public decimal FeeRate(Symbol symbol, DateTime localTime) => 0m;

        public decimal RebateRate(Symbol symbol, DateTime localTime) => 0m;

        public long? ShortableQuantity(Symbol symbol, DateTime localTime)
        {
            var resolver = _easyToBorrowResolver;
            if (resolver == null || symbol.SecurityType != SecurityType.Equity)
            {
                return null;
            }
            try
            {
                return resolver(symbol.Value) == false ? 0 : null;
            }
            catch (Exception e)
            {
                // never block trading because the check failed; the brokerage re-checks before sending
                Log.Error($"TradeZeroShortableProvider: easy-to-borrow check failed for {symbol}: {e.Message}");
                return null;
            }
        }
    }
}
