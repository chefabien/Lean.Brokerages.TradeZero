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

using QuantConnect.Interfaces;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.TradeZero
{
    /// <summary>
    /// TradeZero time-in-force values that have no Lean <see cref="TimeInForce"/> equivalent
    /// </summary>
    public enum TradeZeroTimeInForce
    {
        /// <summary>Fill what is possible immediately, cancel the rest (live only: paper does not support it)</summary>
        ImmediateOrCancel,
        /// <summary>Fill the whole quantity immediately or cancel (live only: paper does not support it)</summary>
        FillOrKill,
        /// <summary>Good until the next market cross (Limit orders)</summary>
        GoodTillCrossing,
        /// <summary>Participate in the opening cross (Market and Limit orders, submitted 04:00-09:25 ET)</summary>
        AtTheOpening
    }

    /// <summary>
    /// TradeZero-specific order properties
    /// </summary>
    /// <example>
    /// Python:
    /// <code>
    /// props = TradeZeroOrderProperties()
    /// props.outside_regular_trading_hours = True   # Limit / StopLimit only: Day -> Day_Plus, GTC -> GTC_Plus
    /// self.limit_order("AAPL", 10, 180.5, order_properties=props)
    /// </code>
    /// </example>
    public class TradeZeroOrderProperties : OrderProperties
    {
        /// <summary>
        /// Allow the order to work in extended hours (04:00-20:00 ET). Only Limit and StopLimit orders:
        /// a Day order is sent as Day_Plus and a GoodTilCanceled order as GTC_Plus.
        /// </summary>
        public bool OutsideRegularTradingHours { get; set; }

        /// <summary>
        /// Route to send the order to (e.g. SMART, ARCA). Empty: the brokerage's configured/default route.
        /// </summary>
        public string Route { get; set; }

        /// <summary>
        /// Overrides <see cref="OrderProperties.TimeInForce"/> with a TradeZero-only value
        /// </summary>
        public TradeZeroTimeInForce? TimeInForceOverride { get; set; }

        public override IOrderProperties Clone()
        {
            return (TradeZeroOrderProperties)MemberwiseClone();
        }
    }
}
