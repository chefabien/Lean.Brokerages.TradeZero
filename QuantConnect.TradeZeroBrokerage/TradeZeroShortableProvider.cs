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

namespace QuantConnect.Brokerages.TradeZero
{
    /// <summary>
    /// Shortable provider of the TradeZero model: no limit at the Lean level (null) for every symbol.
    /// </summary>
    /// <remarks>
    /// Lean's shortable check only sees the symbol, not the order properties. A hard-to-borrow short may be possible through
    /// a locate (<see cref="TradeZeroOrderProperties.LocateHandler"/>, or shares already located), so the brokerage decides
    /// when the order arrives, with a clear message when it refuses. Consequence: <c>self.shortable("HTBX", -100)</c> is
    /// True. For an exact check use <see cref="TradeZeroLocates.IsEasyToBorrow"/> and <see cref="TradeZeroLocates.GetAvailable"/>.
    /// </remarks>
    public class TradeZeroShortableProvider : IShortableProvider
    {
        /// <summary>
        /// Shared instance
        /// </summary>
        public static TradeZeroShortableProvider Instance { get; } = new();

        public decimal FeeRate(Symbol symbol, DateTime localTime) => 0m;

        public decimal RebateRate(Symbol symbol, DateTime localTime) => 0m;

        public long? ShortableQuantity(Symbol symbol, DateTime localTime) => null;
    }
}
