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
using System.Collections.Generic;
using QuantConnect.Brokerages.TradeZero.Api;
using QuantConnect.Orders;
using QuantConnect.Orders.TimeInForces;

namespace QuantConnect.Brokerages.TradeZero
{
    /// <summary>
    /// One TradeZero order needed to execute (part of) a Lean order
    /// </summary>
    /// <param name="Quantity">Shares, always positive</param>
    /// <param name="Side">Buy or Sell</param>
    /// <param name="OpenClose">Open (new or larger position, incl. a short) or Close (reduce a position)</param>
    public record TradeZeroLegPlan(long Quantity, string Side, string OpenClose);

    /// <summary>
    /// Pure translation between Lean orders and TradeZero order payloads (no I/O, unit-tested)
    /// </summary>
    public static class TradeZeroOrderMapper
    {
        /// <summary>TradeZero's maximum quantity per order</summary>
        public const decimal MaxQuantity = 1_000_000m;

        /// <summary>
        /// Lean order types TradeZero can execute
        /// </summary>
        public static readonly IReadOnlySet<OrderType> SupportedOrderTypes = new HashSet<OrderType>
        {
            OrderType.Market, OrderType.Limit, OrderType.StopMarket, OrderType.StopLimit, OrderType.MarketOnOpen
        };

        /// <summary>
        /// Returns why TradeZero cannot take this order, or null if it can
        /// </summary>
        public static string Validate(Order order)
        {
            if (order.SecurityType != SecurityType.Equity)
            {
                return $"TradeZero brokerage only supports US equities (got {order.SecurityType}).";
            }
            if (!SupportedOrderTypes.Contains(order.Type))
            {
                return $"TradeZero does not support {order.Type} orders. Supported: Market, Limit, StopMarket, StopLimit, MarketOnOpen.";
            }
            if (order.Quantity == 0)
            {
                return "Order quantity cannot be zero.";
            }
            if (order.AbsoluteQuantity != Math.Truncate(order.AbsoluteQuantity))
            {
                return $"TradeZero does not support fractional shares (quantity {order.Quantity}).";
            }
            if (order.AbsoluteQuantity > MaxQuantity)
            {
                return $"TradeZero accepts at most {MaxQuantity:N0} shares per order (quantity {order.Quantity}).";
            }
            return GetTimeInForce(order, out var error) == null ? error : null;
        }

        /// <summary>
        /// TradeZero order type for a Lean order type
        /// </summary>
        public static string GetOrderType(OrderType type)
        {
            return type switch
            {
                OrderType.Market => TzValues.Market,
                OrderType.MarketOnOpen => TzValues.Market,
                OrderType.Limit => TzValues.Limit,
                OrderType.StopMarket => TzValues.Stop,
                OrderType.StopLimit => TzValues.StopLimit,
                _ => throw new NotSupportedException($"TradeZero does not support {type} orders")
            };
        }

        /// <summary>
        /// TradeZero time in force for a Lean order, or null with an error message
        /// </summary>
        public static string GetTimeInForce(Order order, out string error)
        {
            error = null;
            var props = order.Properties as TradeZeroOrderProperties;
            var tzType = SupportedOrderTypes.Contains(order.Type) ? GetOrderType(order.Type) : null;

            if (props?.TimeInForceOverride is TradeZeroTimeInForce tif)
            {
                if (order.Type == OrderType.MarketOnOpen)
                {
                    error = "MarketOnOpen orders always use AtTheOpening; remove TimeInForceOverride.";
                    return null;
                }
                if (props.OutsideRegularTradingHours)
                {
                    error = "OutsideRegularTradingHours cannot be combined with TimeInForceOverride.";
                    return null;
                }
                switch (tif)
                {
                    case TradeZeroTimeInForce.ImmediateOrCancel:
                        return TzValues.ImmediateOrCancel;
                    case TradeZeroTimeInForce.FillOrKill:
                        return TzValues.FillOrKill;
                    case TradeZeroTimeInForce.GoodTillCrossing:
                        if (tzType != TzValues.Limit)
                        {
                            error = "GoodTillCrossing is only available for Limit orders.";
                            return null;
                        }
                        return TzValues.GoodTillCrossing;
                    case TradeZeroTimeInForce.AtTheOpening:
                        if (tzType != TzValues.Limit && tzType != TzValues.Market)
                        {
                            error = "AtTheOpening is only available for Market and Limit orders.";
                            return null;
                        }
                        return TzValues.AtTheOpening;
                }
            }

            if (order.Type == OrderType.MarketOnOpen)
            {
                return TzValues.AtTheOpening;
            }

            var outsideRth = props?.OutsideRegularTradingHours == true;
            if (outsideRth && tzType != TzValues.Limit && tzType != TzValues.StopLimit)
            {
                error = "OutsideRegularTradingHours is only available for Limit and StopLimit orders.";
                return null;
            }

            if (order.Type == OrderType.Market)
            {
                // TradeZero only accepts Day on Market orders (R23); Lean's default is GoodTilCanceled
                return TzValues.Day;
            }

            switch (order.TimeInForce)
            {
                case DayTimeInForce:
                    return outsideRth ? TzValues.DayPlus : TzValues.Day;
                case GoodTilCanceledTimeInForce:
                    return outsideRth ? TzValues.GtcPlus : TzValues.GoodTillCancel;
                default:
                    error = $"TradeZero does not support time in force {order.TimeInForce?.GetType().Name}. Use Day or GoodTilCanceled.";
                    return null;
            }
        }

        /// <summary>
        /// Splits a Lean order into TradeZero orders given the current position.
        /// TradeZero needs Buy/Sell + Open/Close: an order that reduces a position closes it, an order on a
        /// flat or same-side position opens. An order crossing zero becomes a closing order followed by an
        /// opening order, placed once the first is filled.
        /// </summary>
        /// <param name="holdings">Current position, negative for a short</param>
        /// <param name="orderQuantity">Lean order quantity, negative to sell</param>
        public static IReadOnlyList<TradeZeroLegPlan> PlanLegs(decimal holdings, decimal orderQuantity)
        {
            if (orderQuantity == 0)
            {
                throw new ArgumentException("Order quantity cannot be zero");
            }
            var side = orderQuantity > 0 ? TzValues.Buy : TzValues.Sell;
            var quantity = (long)Math.Abs(orderQuantity);
            // shares of an opposite position this order can close
            var closable = orderQuantity > 0 ? (long)Math.Max(0m, -holdings) : (long)Math.Max(0m, holdings);
            if (closable == 0)
            {
                return new[] { new TradeZeroLegPlan(quantity, side, TzValues.Open) };
            }
            if (quantity <= closable)
            {
                return new[] { new TradeZeroLegPlan(quantity, side, TzValues.Close) };
            }
            return new[]
            {
                new TradeZeroLegPlan(closable, side, TzValues.Close),
                new TradeZeroLegPlan(quantity - closable, side, TzValues.Open)
            };
        }

        /// <summary>
        /// Builds the POST /order body for one leg of a Lean order
        /// </summary>
        public static TzOrderRequest BuildRequest(Order order, string ticker, TradeZeroLegPlan leg, string clientOrderId, string defaultRoute)
        {
            var timeInForce = GetTimeInForce(order, out var error) ?? throw new NotSupportedException(error);
            var request = new TzOrderRequest
            {
                ClientOrderId = clientOrderId,
                Symbol = ticker,
                SecurityType = TzValues.Stock,
                Side = leg.Side,
                OpenClose = leg.OpenClose,
                OrderType = GetOrderType(order.Type),
                OrderQuantity = leg.Quantity,
                TimeInForce = timeInForce,
            };
            switch (order)
            {
                case LimitOrder limit:
                    request.LimitPrice = limit.LimitPrice.Normalize();
                    break;
                case StopMarketOrder stop:
                    request.StopPrice = stop.StopPrice.Normalize();
                    break;
                case StopLimitOrder stopLimit:
                    request.StopPrice = stopLimit.StopPrice.Normalize();
                    request.LimitPrice = stopLimit.LimitPrice.Normalize();
                    break;
            }
            var route = (order.Properties as TradeZeroOrderProperties)?.Route;
            request.Route = string.IsNullOrWhiteSpace(route) ? (string.IsNullOrWhiteSpace(defaultRoute) ? null : defaultRoute) : route;
            return request;
        }

        /// <summary>
        /// Price of the shares filled since the previous observation of an order, from cumulative figures.
        /// More robust than lastPrice when several fills happened between two polls.
        /// </summary>
        public static decimal IncrementalFillPrice(decimal previousExecuted, decimal previousAveragePrice,
            decimal executed, decimal averagePrice, decimal lastPrice)
        {
            var delta = executed - previousExecuted;
            if (delta <= 0)
            {
                throw new ArgumentException("No new fill");
            }
            if (previousExecuted <= 0)
            {
                return averagePrice > 0 ? averagePrice : lastPrice;
            }
            var price = (averagePrice * executed - previousAveragePrice * previousExecuted) / delta;
            // rounding of priceAvg by the server can make the derived price drift: fall back to sane values
            if (price <= 0 || averagePrice > 0 && Math.Abs(price - averagePrice) > averagePrice * 0.5m)
            {
                return lastPrice > 0 ? lastPrice : averagePrice;
            }
            return Math.Round(price, 4);
        }

        /// <summary>
        /// Converts a working TradeZero order into a Lean order (used by GetOpenOrders after a restart).
        /// The quantity is the part still working (leaves); what was already filled is in the holdings.
        /// </summary>
        public static Order ToLeanOrder(TzOrder tzOrder, Symbol symbol)
        {
            var remaining = tzOrder.LeavesQuantity > 0 ? tzOrder.LeavesQuantity : tzOrder.OrderQuantity - tzOrder.Executed;
            var quantity = tzOrder.IsBuy ? remaining : -remaining;
            var time = tzOrder.StartTime?.ToUniversalTime() ?? DateTime.UtcNow;
            var properties = new TradeZeroOrderProperties { Route = tzOrder.Route };

            switch (tzOrder.TimeInForce)
            {
                case TzValues.GoodTillCancel:
                    properties.TimeInForce = TimeInForce.GoodTilCanceled;
                    break;
                case TzValues.GtcPlus:
                    properties.TimeInForce = TimeInForce.GoodTilCanceled;
                    properties.OutsideRegularTradingHours = true;
                    break;
                case TzValues.DayPlus:
                    properties.TimeInForce = TimeInForce.Day;
                    properties.OutsideRegularTradingHours = true;
                    break;
                case TzValues.GoodTillCrossing:
                    properties.TimeInForce = TimeInForce.GoodTilCanceled;
                    properties.TimeInForceOverride = TradeZeroTimeInForce.GoodTillCrossing;
                    break;
                case TzValues.ImmediateOrCancel:
                    properties.TimeInForce = TimeInForce.Day;
                    properties.TimeInForceOverride = TradeZeroTimeInForce.ImmediateOrCancel;
                    break;
                case TzValues.FillOrKill:
                    properties.TimeInForce = TimeInForce.Day;
                    properties.TimeInForceOverride = TradeZeroTimeInForce.FillOrKill;
                    break;
                default:
                    properties.TimeInForce = TimeInForce.Day;
                    break;
            }

            Order order = tzOrder.OrderType switch
            {
                TzValues.Market when tzOrder.TimeInForce == TzValues.AtTheOpening => new MarketOnOpenOrder(symbol, quantity, time, properties: properties),
                TzValues.Market => new MarketOrder(symbol, quantity, time, properties: properties),
                TzValues.Limit => new LimitOrder(symbol, quantity, tzOrder.LimitPrice, time, properties: properties),
                TzValues.Stop => new StopMarketOrder(symbol, quantity, tzOrder.PriceStop, time, properties: properties),
                TzValues.StopLimit => new StopLimitOrder(symbol, quantity, tzOrder.PriceStop, tzOrder.LimitPrice, time, properties: properties),
                _ => null
            };
            if (order == null)
            {
                return null;
            }
            if (tzOrder.OrderType == TzValues.Limit && tzOrder.TimeInForce == TzValues.AtTheOpening)
            {
                properties.TimeInForceOverride = TradeZeroTimeInForce.AtTheOpening;
            }
            order.BrokerId.Add(tzOrder.ClientOrderId);
            order.Status = tzOrder.Executed > 0 ? OrderStatus.PartiallyFilled : OrderStatus.Submitted;
            return order;
        }
    }
}
