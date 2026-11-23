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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.TradeZero.Tests
{
    /// <summary>
    /// Brokerage behaviour against tz-mock scenarios (see tz-mock/README.md). Each test uses its own mock session.
    /// The mock clock starts on 2026-01-05 09:31 ET; AAPL trades around 250 that morning.
    /// </summary>
    [TestFixture, Category("TzMock"), NonParallelizable]
    public class TradeZeroBrokerageMockTests
    {
        private static readonly Symbol Aapl = TradeZeroHarness.Equity("AAPL");
        private static DateTime Now => DateTime.UtcNow;

        [Test]
        public void ConnectsToPaperAccountAndPublishesAccountSnapshot()
        {
            using var h = new TradeZeroHarness();
            Assert.IsTrue(h.Brokerage.IsConnected);
            var account = TradeZeroAccount.Current;
            Assert.IsNotNull(account);
            Assert.AreEqual(h.Brokerage.AccountId, account.AccountId);
            Assert.IsTrue(account.IsPaper);
            Assert.AreEqual(1_000_000m, account.AccountValue);
            Assert.AreEqual(1_000_000m, account.BuyingPower);
            // the mock (like paper) allows 1x, the model defaults to 4x intraday
            Assert.IsTrue(h.Messages.Any(m => m.Code == "LeverageMismatch"));

            h.Brokerage.Disconnect();
            Assert.IsFalse(h.Brokerage.IsConnected);
            Assert.IsNull(TradeZeroAccount.Current);
        }

        [Test]
        public void RefusesPaperAccountWhenConfiguredForLive()
        {
            using var h = new TradeZeroHarness(environment: TradeZeroEnvironment.Live, connect: false);
            var e = Assert.Throws<InvalidOperationException>(() => h.Brokerage.Connect());
            StringAssert.Contains("Refusing to connect", e.Message);
            Assert.IsFalse(h.Brokerage.IsConnected);
        }

        [Test]
        public void MarketBuyFillsAndUpdatesHoldingsAndCash()
        {
            using var h = new TradeZeroHarness();
            var price = TzMock.Price(h.Key, "AAPL");
            var order = new MarketOrder(Aapl, 10, Now);
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Filled);

            var fill = h.EventsFor(order).Single(e => e.Status == OrderStatus.Filled);
            Assert.AreEqual(10m, fill.FillQuantity);
            Assert.AreEqual(price, fill.FillPrice);
            Assert.AreEqual(OrderStatus.Submitted, h.EventsFor(order).First().Status);
            Assert.AreEqual(1, order.BrokerId.Count);

            var holding = h.Brokerage.GetAccountHoldings().Single();
            Assert.AreEqual(Aapl, holding.Symbol);
            Assert.AreEqual(10m, holding.Quantity);
            Assert.AreEqual(price, holding.AveragePrice);

            var cash = h.Brokerage.GetCashBalance().Single();
            Assert.AreEqual(Currencies.USD, cash.Currency);
            Assert.AreEqual(1_000_000m - 10 * price, cash.Amount);
        }

        [Test]
        public void ShortSaleOfEasyToBorrowStock()
        {
            using var h = new TradeZeroHarness("short_htb");
            var price = TzMock.Price(h.Key, "AAPL");
            var order = new MarketOrder(Aapl, -10, Now);
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Filled);
            Assert.AreEqual(-10m, h.FilledQuantity(order));

            Assert.AreEqual(-10m, h.Brokerage.GetAccountHoldings().Single().Quantity);
            // short proceeds are cash for Lean: total portfolio value = cash + (-10 * price) = account value
            Assert.AreEqual(1_000_000m + 10 * price, h.Brokerage.GetCashBalance().Single().Amount);
        }

        [Test]
        public void ShortSaleOfHardToBorrowStockIsRefusedBeforeSending()
        {
            using var h = new TradeZeroHarness("short_htb");
            var order = new MarketOrder(TradeZeroHarness.Equity("HTBX"), -100, Now);
            Assert.IsFalse(h.Place(order));
            var invalid = h.EventsFor(order).Single();
            Assert.AreEqual(OrderStatus.Invalid, invalid.Status);
            StringAssert.Contains("hard to borrow", invalid.Message);
            Assert.AreEqual(0, TzMock.State(h.Key)["terminalOrders"]!.Value<int>());
            Assert.IsEmpty(TzMock.State(h.Key)["workingOrders"]!);
        }

        [Test]
        public void ShortableProviderLeavesHardToBorrowToTheBrokerage()
        {
            using var h = new TradeZeroHarness("short_htb");
            // Lean cannot see the locate handler: the brokerage decides when the order arrives
            Assert.IsNull(TradeZeroShortableProvider.Instance.ShortableQuantity(Aapl, Now));
            Assert.IsNull(TradeZeroShortableProvider.Instance.ShortableQuantity(TradeZeroHarness.Equity("HTBX"), Now));
            Assert.AreEqual(true, TradeZeroLocates.IsEasyToBorrow(Aapl));
            Assert.AreEqual(false, TradeZeroLocates.IsEasyToBorrow(TradeZeroHarness.Equity("HTBX")));
        }

        [Test]
        public void RestingLimitFillsWhenPriceIsReached()
        {
            using var h = new TradeZeroHarness();
            // AAPL trades down to ~248.9 within 30 minutes
            var order = new LimitOrder(Aapl, 10, 249.5m, Now);
            Assert.IsTrue(h.Place(order));
            h.Brokerage.PollOnce(force: true);
            Assert.AreEqual(OrderStatus.Submitted, h.StatusOf(order));

            h.Advance(TimeSpan.FromMinutes(30));
            h.WaitForStatus(order, OrderStatus.Filled);
            Assert.LessOrEqual(h.EventsFor(order).Single(e => e.Status == OrderStatus.Filled).FillPrice, 249.5m);
            Assert.AreEqual(10m, h.Holdings("AAPL"));
        }

        [Test]
        public void CancelsWorkingOrder()
        {
            using var h = new TradeZeroHarness();
            var order = new LimitOrder(Aapl, 10, 200m, Now);
            Assert.IsTrue(h.Place(order));
            Assert.IsTrue(h.Brokerage.CancelOrder(order));
            h.WaitForStatus(order, OrderStatus.Canceled);
            Assert.IsEmpty(TzMock.State(h.Key)["workingOrders"]!);
            Assert.IsFalse(h.Brokerage.CancelOrder(order), "a second cancel must be refused");
        }

        [Test]
        public void DayOrderExpiresAtTheClose()
        {
            using var h = new TradeZeroHarness();
            var order = new LimitOrder(Aapl, 10, 200m, Now, properties: new OrderProperties { TimeInForce = TimeInForce.Day });
            Assert.IsTrue(h.Place(order));
            h.AdvanceTo(new DateTime(2026, 1, 5, 21, 1, 0, DateTimeKind.Utc));
            h.WaitForStatus(order, OrderStatus.Canceled);
            StringAssert.Contains("Expired", h.EventsFor(order).Last().Message);
        }

        [Test]
        public void GtcOrderSurvivesTheClose()
        {
            using var h = new TradeZeroHarness();
            var order = new LimitOrder(Aapl, 10, 200m, Now, properties: new OrderProperties { TimeInForce = TimeInForce.GoodTilCanceled });
            Assert.IsTrue(h.Place(order));
            h.AdvanceTo(new DateTime(2026, 1, 6, 14, 35, 0, DateTimeKind.Utc));
            Assert.AreEqual(OrderStatus.Submitted, h.StatusOf(order));
            Assert.AreEqual(1, TzMock.State(h.Key)["workingOrders"]!.Count());
        }

        [Test]
        public void UpdateReplacesTheOrderAndKeepsOneLeanOrder()
        {
            using var h = new TradeZeroHarness();
            var order = new LimitOrder(Aapl, 10, 200m, Now);
            Assert.IsTrue(h.Place(order));
            var firstId = order.BrokerId.Single();

            // move the limit above the market: the replacement fills at once
            Assert.IsTrue(h.Update(order, new UpdateOrderFields { LimitPrice = 260m }));
            Assert.AreEqual(2, order.BrokerId.Count);
            Assert.AreNotEqual(firstId, order.BrokerId[1]);
            Assert.IsTrue(h.EventsFor(order).Any(e => e.Status == OrderStatus.UpdateSubmitted));
            h.WaitForStatus(order, OrderStatus.Filled);
            Assert.AreEqual(10m, h.FilledQuantity(order));
            Assert.IsFalse(h.EventsFor(order).Any(e => e.Status == OrderStatus.Canceled), "the replaced leg's cancel must not reach Lean");
        }

        [Test]
        public void UpdateAfterPartialFillOnlyReplacesTheRemainder()
        {
            // THIN: at most 10% of each bar's volume fills (30-50 shares per minute)
            using var h = new TradeZeroHarness("partial_fills");
            var order = new LimitOrder(TradeZeroHarness.Equity("THIN"), 400, 13m, Now);
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.PartiallyFilled);
            var filledBefore = h.FilledQuantity(order);
            Assert.Greater(filledBefore, 0m);
            Assert.Less(filledBefore, 100m);

            Assert.IsTrue(h.Update(order, new UpdateOrderFields { Quantity = 100 }));
            var replacement = h.Brokerage.GetOpenOrders().Single();
            Assert.AreEqual(order.BrokerId.Last(), replacement.BrokerId.Single());
            Assert.AreEqual(100m - filledBefore, replacement.Quantity);

            for (var i = 0; i < 10 && h.StatusOf(order) != OrderStatus.Filled; i++)
            {
                h.Advance(TimeSpan.FromMinutes(1));
            }
            h.WaitForStatus(order, OrderStatus.Filled);
            Assert.AreEqual(100m, h.FilledQuantity(order));
            Assert.AreEqual(100m, h.Holdings("THIN"));
        }

        [Test]
        public void UpdateOfFilledOrderIsRefused()
        {
            using var h = new TradeZeroHarness();
            var order = new LimitOrder(Aapl, 10, 260m, Now);
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Filled);
            Assert.IsFalse(h.Update(order, new UpdateOrderFields { LimitPrice = 255m }));
            Assert.IsTrue(h.Messages.Any(m => m.Code == "UpdateRejected"));
        }

        [Test]
        public void RejectedReplacementCancelsTheLeanOrder()
        {
            using var h = new TradeZeroHarness();
            var order = new LimitOrder(Aapl, 10, 200m, Now);
            Assert.IsTrue(h.Place(order));
            // a negative price is rejected synchronously by TradeZero (R145)
            Assert.IsFalse(h.Update(order, new UpdateOrderFields { LimitPrice = -1m }));
            h.WaitForStatus(order, OrderStatus.Canceled);
            StringAssert.Contains("R145", h.EventsFor(order).Last().Message);
            Assert.IsEmpty(TzMock.State(h.Key)["workingOrders"]!);
        }

        [Test]
        public void OrderCrossingZeroClosesThenOpens()
        {
            using var h = new TradeZeroHarness();
            var buy = new MarketOrder(Aapl, 10, Now);
            Assert.IsTrue(h.Place(buy));
            h.WaitForStatus(buy, OrderStatus.Filled);

            var sell = new MarketOrder(Aapl, -25, Now);
            Assert.IsTrue(h.Place(sell));
            h.WaitForStatus(sell, OrderStatus.Filled);

            Assert.AreEqual(2, sell.BrokerId.Count, "a closing order then an opening order");
            Assert.AreEqual(-25m, h.FilledQuantity(sell));
            Assert.AreEqual(-15m, h.Holdings("AAPL"));
            Assert.AreEqual(-15m, h.Brokerage.GetAccountHoldings().Single().Quantity);
            var statuses = h.EventsFor(sell).Select(e => e.Status).ToList();
            CollectionAssert.AreEqual(new[] { OrderStatus.Submitted, OrderStatus.PartiallyFilled, OrderStatus.Filled }, statuses);
        }

        [Test]
        public void AsyncRejectionMakesTheOrderInvalid()
        {
            using var h = new TradeZeroHarness(scenarioParameters: new { cash = 10_000 });
            var order = new MarketOrder(Aapl, 1_000, Now);
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Invalid);
            StringAssert.Contains("R100", h.EventsFor(order).Last().Message);
        }

        [Test]
        public void UnsupportedOrdersNeverReachTradeZero()
        {
            using var h = new TradeZeroHarness();
            var order = new TrailingStopOrder(Aapl, 10, 240m, 1m, false, Now);
            Assert.IsFalse(h.Place(order));
            Assert.AreEqual(OrderStatus.Invalid, h.EventsFor(order).Single().Status);
            Assert.AreEqual(0, TzMock.State(h.Key)["terminalOrders"]!.Value<int>());
        }

        [Test]
        public void RestartedAlgorithmTakesOverOpenOrders()
        {
            string key;
            string brokerId;
            using (var first = new TradeZeroHarness())
            {
                key = first.Key;
                var order = new LimitOrder(Aapl, 10, 200m, Now, properties: new OrderProperties { TimeInForce = TimeInForce.GoodTilCanceled });
                Assert.IsTrue(first.Place(order));
                brokerId = order.BrokerId.Single();
            }

            // same mock session (same key), new brokerage instance: like Lean restarting
            using var second = new TradeZeroHarness(scenario: null, key: key);
            var open = second.Brokerage.GetOpenOrders();
            var recovered = open.Single();
            Assert.AreEqual(brokerId, recovered.BrokerId.Single());
            Assert.AreEqual(10m, recovered.Quantity);
            Assert.AreEqual(200m, ((LimitOrder)recovered).LimitPrice);
            Assert.AreEqual(TimeInForce.GoodTilCanceled, recovered.TimeInForce);

            second.Orders.Add(recovered);
            Assert.IsTrue(second.Brokerage.CancelOrder(recovered));
            second.WaitForStatus(recovered, OrderStatus.Canceled);
            Assert.IsFalse(second.Messages.Any(m => m.Code == "UnknownOrder"));
        }

        [Test]
        public void ApiLogRecordsTheSessionWithoutSecrets()
        {
            var path = Path.Combine(Path.GetTempPath(), $"tz-mock-api-{Guid.NewGuid():N}.jsonl");
            var key = TzMock.NewKey("KEYID");
            const string secret = "SECRET-do-not-log-7f3a";
            try
            {
                using (var h = new TradeZeroHarness(key: key, secret: secret, apiLogFile: path))
                {
                    var order = new MarketOrder(Aapl, 5, Now);
                    Assert.IsTrue(h.Place(order));
                    h.WaitForStatus(order, OrderStatus.Filled);
                }
                var text = File.ReadAllText(path);
                StringAssert.DoesNotContain(key, text);
                StringAssert.DoesNotContain(secret, text);

                var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(JObject.Parse).ToList();
                Assert.IsTrue(lines.Any(l => (string)l["kind"] == "state" && (string)l["msg"] == "connected"));
                Assert.IsTrue(lines.Any(l => (string)l["kind"] == "http" && (string)l["method"] == "POST" && ((string)l["path"]).EndsWith("/order")));
                Assert.IsTrue(lines.Any(l => (string)l["kind"] == "lean" && (string)l["msg"] == "submit leg"));
                Assert.IsTrue(lines.Any(l => (string)l["kind"] == "lean" && (string)l["msg"] == "fill"));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void HandlerPlacingOrdersOnAFillIsNotBlockedByTheBrokerage()
        {
            // Lean runs the algorithm's OnOrderEvent inside the brokerage's callback, and places the orders it sends on
            // its transaction thread while the handler waits (WaitForOrderSubmission, 1 s each). Raised while the
            // brokerage held its order lock, a fill made each of those PlaceOrder calls wait for the handler instead.
            using var h = new TradeZeroHarness();
            var entry = new MarketOrder(Aapl, 10, Now);
            var exits = Enumerable.Range(1, 5).Select(i => (Order)new LimitOrder(Aapl, -2, 300m + i, Now)).ToList();
            exits.ForEach(h.Orders.Add);
            var took = new List<TimeSpan>();
            h.Brokerage.OrdersStatusChanged += (_, events) =>
            {
                if (!events.Any(e => e.OrderId == entry.Id && e.Status == OrderStatus.Filled))
                {
                    return;
                }
                foreach (var exit in exits)
                {
                    var watch = Stopwatch.StartNew();
                    var placed = Task.Run(() => h.Brokerage.PlaceOrder(exit));
                    var done = placed.Wait(TimeSpan.FromSeconds(3)) && placed.Result;
                    lock (took)
                    {
                        took.Add(done ? watch.Elapsed : TimeSpan.MaxValue);
                    }
                }
            };

            Assert.IsTrue(h.Place(entry));
            h.WaitForStatus(entry, OrderStatus.Filled);
            h.WaitFor(() => { lock (took) { return took.Count == exits.Count; } }, "the handler to place the exits", 30);
            lock (took)
            {
                Assert.IsTrue(took.All(t => t < TimeSpan.FromSeconds(3)), $"PlaceOrder calls from the handler: {string.Join(", ", took)}");
            }
            foreach (var exit in exits)
            {
                h.WaitForStatus(exit, OrderStatus.Submitted);
                Assert.AreEqual(OrderStatus.Submitted, h.EventsFor(exit).First().Status);
            }
        }

        [Test]
        public void OrdersArePlannedFromFillsNotDeliveredToLeanYet()
        {
            var path = Path.Combine(Path.GetTempPath(), $"tz-mock-api-{Guid.NewGuid():N}.jsonl");
            var gate = new ManualResetEventSlim(false);
            var entry = new MarketOrder(Aapl, 10, Now);
            var exit = new MarketOrder(Aapl, -10, Now);
            try
            {
                using (var h = new TradeZeroHarness(apiLogFile: path))
                {
                    // a slow handler holds the delivery of everything after the entry's Submitted, its fill included
                    h.Brokerage.OrdersStatusChanged += (_, events) =>
                    {
                        if (events.Any(e => e.OrderId == entry.Id && e.Status == OrderStatus.Submitted))
                        {
                            gate.Wait(TimeSpan.FromSeconds(20));
                        }
                    };
                    try
                    {
                        Assert.IsTrue(h.Place(entry));
                        // not h.Holdings: it waits for the deliveries
                        var lean = h.Securities.GetSecurity(Aapl).Holdings;
                        var watch = Stopwatch.StartNew();
                        while (h.Brokerage.HoldingsQuantity(Aapl) != 10m && watch.Elapsed < TimeSpan.FromSeconds(10))
                        {
                            h.Brokerage.PollOnce(force: true);
                            Thread.Sleep(50);
                        }
                        Assert.AreEqual(10m, h.Brokerage.HoldingsQuantity(Aapl));
                        Assert.AreEqual(0m, lean.Quantity, "the fill must not be delivered yet");

                        h.Orders.Add(exit);
                        Assert.IsTrue(h.Brokerage.PlaceOrder(exit));
                    }
                    finally
                    {
                        gate.Set();
                    }
                    h.WaitForStatus(exit, OrderStatus.Filled);
                    Assert.AreEqual(0m, h.Holdings("AAPL"));
                }

                var legs = File.ReadAllLines(path).Select(JObject.Parse)
                    .Where(l => (string)l["msg"] == "submit leg" && (int)l["data"]!["leanOrderId"]! == exit.Id)
                    .ToList();
                Assert.AreEqual(1, legs.Count);
                Assert.AreEqual("Close", (string)legs[0]["data"]!["request"]!["openClose"], "selling a long held at the broker closes it");
            }
            finally
            {
                gate.Set();
                File.Delete(path);
            }
        }
    }
}
