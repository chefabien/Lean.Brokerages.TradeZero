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
using System.Linq;
using NUnit.Framework;
using QuantConnect.Brokerages.TradeZero.Api;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.TradeZero.Tests
{
    [TestFixture]
    public class TradeZeroOrderMapperTests
    {
        private static readonly Symbol Aapl = TradeZeroHarness.Equity("AAPL");
        private static readonly DateTime Time = new(2026, 1, 5, 15, 0, 0, DateTimeKind.Utc);

        private static OrderProperties Props(TimeInForce tif, bool outsideRth = false, TradeZeroTimeInForce? overrideTif = null, string route = null)
        {
            return new TradeZeroOrderProperties { TimeInForce = tif, OutsideRegularTradingHours = outsideRth, TimeInForceOverride = overrideTif, Route = route };
        }

        // ---------------------------------------------------------------- legs (Buy/Sell + Open/Close)

        [TestCase(0, 10, "Buy:10:Open")]
        [TestCase(5, 10, "Buy:10:Open")]
        [TestCase(-20, 10, "Buy:10:Close")]
        [TestCase(-10, 10, "Buy:10:Close")]
        [TestCase(-4, 10, "Buy:4:Close|Buy:6:Open")]
        [TestCase(0, -10, "Sell:10:Open")]
        [TestCase(-5, -10, "Sell:10:Open")]
        [TestCase(20, -10, "Sell:10:Close")]
        [TestCase(10, -10, "Sell:10:Close")]
        [TestCase(10, -25, "Sell:10:Close|Sell:15:Open")]
        public void PlansLegsFromHoldings(decimal holdings, decimal quantity, string expected)
        {
            var legs = TradeZeroOrderMapper.PlanLegs(holdings, quantity);
            Assert.AreEqual(expected, string.Join("|", legs.Select(l => $"{l.Side}:{l.Quantity}:{l.OpenClose}")));
        }

        // ---------------------------------------------------------------- order types and time in force

        [Test]
        public void MarketOrderIsAlwaysDay()
        {
            var order = new MarketOrder(Aapl, 10, Time, properties: Props(TimeInForce.GoodTilCanceled));
            var request = Build(order);
            Assert.AreEqual(TzValues.Market, request.OrderType);
            Assert.AreEqual(TzValues.Day, request.TimeInForce);
            Assert.IsNull(request.LimitPrice);
            Assert.IsNull(request.StopPrice);
        }

        [TestCase(true, TzValues.GoodTillCancel)]
        [TestCase(false, TzValues.Day)]
        public void LimitOrder(bool gtc, string expectedTif)
        {
            var order = new LimitOrder(Aapl, 10, 250.5m, Time, properties: Props(gtc ? TimeInForce.GoodTilCanceled : TimeInForce.Day));
            var request = Build(order);
            Assert.AreEqual(TzValues.Limit, request.OrderType);
            Assert.AreEqual(expectedTif, request.TimeInForce);
            Assert.AreEqual(250.5m, request.LimitPrice);
            Assert.IsNull(request.StopPrice);
        }

        [Test]
        public void StopMarketOrderIsStop()
        {
            var request = Build(new StopMarketOrder(Aapl, -10, 240m, Time, properties: Props(TimeInForce.Day)));
            Assert.AreEqual(TzValues.Stop, request.OrderType);
            Assert.AreEqual(240m, request.StopPrice);
            Assert.IsNull(request.LimitPrice);
        }

        [Test]
        public void StopLimitOrderHasBothPrices()
        {
            var request = Build(new StopLimitOrder(Aapl, 10, 251m, 252m, Time, properties: Props(TimeInForce.GoodTilCanceled)));
            Assert.AreEqual(TzValues.StopLimit, request.OrderType);
            Assert.AreEqual(251m, request.StopPrice);
            Assert.AreEqual(252m, request.LimitPrice);
            Assert.AreEqual(TzValues.GoodTillCancel, request.TimeInForce);
        }

        [Test]
        public void MarketOnOpenIsMarketAtTheOpening()
        {
            var request = Build(new MarketOnOpenOrder(Aapl, 10, Time));
            Assert.AreEqual(TzValues.Market, request.OrderType);
            Assert.AreEqual(TzValues.AtTheOpening, request.TimeInForce);
        }

        [TestCase(false, TzValues.DayPlus)]
        [TestCase(true, TzValues.GtcPlus)]
        public void ExtendedHoursLimit(bool gtc, string expectedTif)
        {
            var order = new LimitOrder(Aapl, 10, 250m, Time, properties: Props(gtc ? TimeInForce.GoodTilCanceled : TimeInForce.Day, outsideRth: true));
            Assert.AreEqual(expectedTif, Build(order).TimeInForce);
        }

        [Test]
        public void ExtendedHoursRefusedOnMarketAndStop()
        {
            Assert.IsNotNull(TradeZeroOrderMapper.Validate(new MarketOrder(Aapl, 10, Time, properties: Props(TimeInForce.Day, outsideRth: true))));
            Assert.IsNotNull(TradeZeroOrderMapper.Validate(new StopMarketOrder(Aapl, 10, 251m, Time, properties: Props(TimeInForce.Day, outsideRth: true))));
        }

        [TestCase(TradeZeroTimeInForce.ImmediateOrCancel, TzValues.ImmediateOrCancel)]
        [TestCase(TradeZeroTimeInForce.FillOrKill, TzValues.FillOrKill)]
        [TestCase(TradeZeroTimeInForce.GoodTillCrossing, TzValues.GoodTillCrossing)]
        [TestCase(TradeZeroTimeInForce.AtTheOpening, TzValues.AtTheOpening)]
        public void TimeInForceOverrideOnLimit(TradeZeroTimeInForce tif, string expected)
        {
            var order = new LimitOrder(Aapl, 10, 250m, Time, properties: Props(TimeInForce.Day, overrideTif: tif));
            Assert.AreEqual(expected, Build(order).TimeInForce);
        }

        [Test]
        public void GoodTillCrossingOnlyForLimit()
        {
            var order = new StopMarketOrder(Aapl, 10, 251m, Time, properties: Props(TimeInForce.Day, overrideTif: TradeZeroTimeInForce.GoodTillCrossing));
            StringAssert.Contains("GoodTillCrossing", TradeZeroOrderMapper.Validate(order));
        }

        [Test]
        public void RouteFromPropertiesThenDefault()
        {
            var withRoute = new LimitOrder(Aapl, 10, 250m, Time, properties: Props(TimeInForce.Day, route: "ARCA"));
            Assert.AreEqual("ARCA", TradeZeroOrderMapper.BuildRequest(withRoute, "AAPL", new TradeZeroLegPlan(10, "Buy", "Open"), "c1", "SMART").Route);
            var withoutRoute = new LimitOrder(Aapl, 10, 250m, Time, properties: Props(TimeInForce.Day));
            Assert.AreEqual("SMART", TradeZeroOrderMapper.BuildRequest(withoutRoute, "AAPL", new TradeZeroLegPlan(10, "Buy", "Open"), "c1", "SMART").Route);
            Assert.IsNull(TradeZeroOrderMapper.BuildRequest(withoutRoute, "AAPL", new TradeZeroLegPlan(10, "Buy", "Open"), "c1", null).Route);
        }

        [Test]
        public void RequestSerializesLikeTheApi()
        {
            var request = Build(new MarketOrder(Aapl, -3, Time));
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(request);
            StringAssert.Contains("\"orderQuantity\":3", json);
            StringAssert.Contains("\"securityType\":\"Stock\"", json);
            StringAssert.DoesNotContain("limitPrice", json);
            StringAssert.DoesNotContain("route", json);
        }

        // ---------------------------------------------------------------- validation

        [Test]
        public void RejectsUnsupportedOrders()
        {
            StringAssert.Contains("fractional", TradeZeroOrderMapper.Validate(new MarketOrder(Aapl, 1.5m, Time)));
            StringAssert.Contains("TrailingStop", TradeZeroOrderMapper.Validate(new TrailingStopOrder(Aapl, 10, 240m, 1m, false, Time)));
            StringAssert.Contains("MarketOnClose", TradeZeroOrderMapper.Validate(new MarketOnCloseOrder(Aapl, 10, Time)));
            StringAssert.Contains("at most", TradeZeroOrderMapper.Validate(new MarketOrder(Aapl, 2_000_000, Time)));
            var option = Symbol.CreateOption(Aapl, Market.USA, OptionStyle.American, OptionRight.Call, 250m, new DateTime(2026, 6, 19));
            StringAssert.Contains("equities", TradeZeroOrderMapper.Validate(new MarketOrder(option, 1, Time)));
            var gtd = new LimitOrder(Aapl, 10, 250m, Time, properties: Props(TimeInForce.GoodTilDate(new DateTime(2026, 2, 1))));
            StringAssert.Contains("time in force", TradeZeroOrderMapper.Validate(gtd));
        }

        [Test]
        public void AcceptsSupportedOrders()
        {
            Assert.IsNull(TradeZeroOrderMapper.Validate(new MarketOrder(Aapl, -100, Time)));
            Assert.IsNull(TradeZeroOrderMapper.Validate(new LimitOrder(Aapl, 100, 250m, Time)));
            Assert.IsNull(TradeZeroOrderMapper.Validate(new StopMarketOrder(Aapl, 100, 250m, Time)));
            Assert.IsNull(TradeZeroOrderMapper.Validate(new StopLimitOrder(Aapl, 100, 250m, 251m, Time)));
            Assert.IsNull(TradeZeroOrderMapper.Validate(new MarketOnOpenOrder(Aapl, 100, Time)));
        }

        // ---------------------------------------------------------------- fills

        [TestCase(0, 0, 100, 10.5, 10.4, 10.5)]
        [TestCase(100, 10, 200, 11, 12, 12)]
        [TestCase(30, 12.01, 60, 12.015, 12.02, 12.02)]
        public void IncrementalFillPrice(decimal prevExec, decimal prevAvg, decimal exec, decimal avg, decimal last, decimal expected)
        {
            Assert.AreEqual(expected, TradeZeroOrderMapper.IncrementalFillPrice(prevExec, prevAvg, exec, avg, last));
        }

        // ---------------------------------------------------------------- open orders after a restart

        [Test]
        public void ConvertsWorkingTradeZeroOrders()
        {
            var row = new TzOrder
            {
                ClientOrderId = "abc.1", Symbol = "AAPL", Side = "SellShort", OpenClose = "Open", OrderType = TzValues.Limit,
                TimeInForce = TzValues.GtcPlus, OrderQuantity = 100, Executed = 40, LeavesQuantity = 60, LimitPrice = 251.5m,
                OrderStatus = TzOrderStatus.PartiallyFilled, StartTime = Time
            };
            var order = TradeZeroOrderMapper.ToLeanOrder(row, Aapl);
            Assert.IsInstanceOf<LimitOrder>(order);
            Assert.AreEqual(-60m, order.Quantity);
            Assert.AreEqual(251.5m, ((LimitOrder)order).LimitPrice);
            Assert.AreEqual(TimeInForce.GoodTilCanceled, order.TimeInForce);
            Assert.IsTrue(((TradeZeroOrderProperties)order.Properties).OutsideRegularTradingHours);
            Assert.AreEqual("abc.1", order.BrokerId.Single());
            Assert.AreEqual(OrderStatus.PartiallyFilled, order.Status);

            var moo = TradeZeroOrderMapper.ToLeanOrder(new TzOrder { ClientOrderId = "x", Side = "Buy", OrderType = TzValues.Market, TimeInForce = TzValues.AtTheOpening, OrderQuantity = 5, LeavesQuantity = 5 }, Aapl);
            Assert.IsInstanceOf<MarketOnOpenOrder>(moo);
            Assert.AreEqual(5m, moo.Quantity);
        }

        private static TzOrderRequest Build(Order order)
        {
            Assert.IsNull(TradeZeroOrderMapper.Validate(order));
            var leg = TradeZeroOrderMapper.PlanLegs(0, order.Quantity)[0];
            return TradeZeroOrderMapper.BuildRequest(order, "AAPL", leg, "cid", null);
        }
    }
}
