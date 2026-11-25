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
using Newtonsoft.Json;
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

        /// <summary>
        /// Called when this order shorts a hard-to-borrow symbol and needs more located shares than the account holds, with
        /// every offer TradeZero made for the quote (1 offer, or 2 for a Reg SHO threshold symbol: PreBorrow and SingleUse).
        /// Return the offer to accept (its cost is charged right away and added to the first fill's fee), or null to decline
        /// them all. Without a handler, a short that needs a new locate is refused (Invalid).
        /// </summary>
        /// <remarks>
        /// Runs on a connector thread, not on the algorithm thread: keep it short and pure (read the offers, compare, return)
        /// and never place orders from it. Offers expire 30 s after they are made. It must return one of the objects in
        /// <c>quote.Offers</c> or null: an exception, or another object, counts as a decline. Called at most once per quote.
        /// Not serialized: an order recovered after a restart has no handler (it was already placed).
        /// <code>
        /// props.locate_handler = lambda q: next((o for o in q.offers if o.price_per_share &lt;= 0.05), None)
        /// </code>
        /// </remarks>
        [JsonIgnore]
        public Func<TradeZeroLocateQuote, TradeZeroLocateOffer> LocateHandler { get; set; }

        /// <summary>
        /// Don't warn when a hard-to-borrow short is not a multiple of 100 shares (default false: warn). Locates are sold,
        /// and sold back, in lots of 100, so the rest of the lot stays unused.
        /// </summary>
        public bool SuppressOddLotLocateWarning { get; set; }

        public override IOrderProperties Clone()
        {
            return (TradeZeroOrderProperties)MemberwiseClone();
        }
    }
}
