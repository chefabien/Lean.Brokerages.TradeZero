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
using System.IO;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Orders;
using QuantConnect.Tests.Brokerages;

namespace QuantConnect.Brokerages.TradeZero.Tests
{
    /// <summary>
    /// Checks against the real TradeZero PAPER API, run by hand only:
    ///   dotnet test --filter TestCategory=Paper
    /// Keys come from TZ_API_KEY_ID / TZ_API_SECRET_KEY. The brokerage's environment guard refuses to connect
    /// unless the account type is "Paper", so these tests can never touch a live account.
    /// The API exchanges are logged to bin/.../tradezero-paper-api.jsonl (keys masked) for inspection.
    /// </summary>
    [TestFixture, Explicit("Calls the real TradeZero paper API"), Category("Paper"), NonParallelizable]
    public class TradeZeroPaperTests
    {
        private OrderProvider _orders;
        private SecurityProvider _securities;
        private TradeZeroBrokerage _brokerage;

        [SetUp]
        public void SetUp()
        {
            TestSetup.EnsureInitialized();
            var key = Environment.GetEnvironmentVariable(TradeZeroSettings.ApiKeyIdEnvironmentVariable);
            var secret = Environment.GetEnvironmentVariable(TradeZeroSettings.ApiSecretKeyEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
            {
                Assert.Ignore("TZ_API_KEY_ID / TZ_API_SECRET_KEY not set");
            }
            var settings = new TradeZeroSettings
            {
                ApiKeyId = key,
                ApiSecretKey = secret,
                Environment = TradeZeroEnvironment.Paper,
                ApiLogFile = Path.Combine(TestContext.CurrentContext.TestDirectory, "tradezero-paper-api.jsonl"),
            };
            _orders = new OrderProvider();
            _securities = new SecurityProvider();
            _brokerage = new TradeZeroBrokerage(settings, _orders, _securities);
        }

        [TearDown]
        public void TearDown()
        {
            _brokerage?.Dispose();
        }

        /// <summary>
        /// Read-only: connect (paper guard), account snapshot, cash, holdings, open orders
        /// </summary>
        [Test]
        public void ReadsPaperAccount()
        {
            _brokerage.Connect();
            Assert.IsTrue(_brokerage.IsConnected);
            var account = TradeZeroAccount.Current;
            Assert.IsTrue(account.IsPaper);
            Assert.Greater(account.AccountValue, 0m);
            TestContext.Out.WriteLine(account.ToString());

            var cash = _brokerage.GetCashBalance().Single();
            TestContext.Out.WriteLine($"Lean cash: {cash.Amount} {cash.Currency}");
            foreach (var holding in _brokerage.GetAccountHoldings())
            {
                TestContext.Out.WriteLine($"Holding: {holding}");
            }
            foreach (var order in _brokerage.GetOpenOrders())
            {
                TestContext.Out.WriteLine($"Open order: {order.BrokerId.Single()} {order}");
            }
            var shortable = TradeZeroShortableProvider.Instance.ShortableQuantity(TradeZeroHarness.Equity("AAPL"), DateTime.UtcNow);
            TestContext.Out.WriteLine($"AAPL shortable quantity: {(shortable.HasValue ? shortable.ToString() : "unlimited (easy to borrow)")}");
        }

        /// <summary>
        /// Places a 1-share AAPL buy limit at $1 (cannot fill), then cancels it. Also checks that TradeZero
        /// accepts this brokerage's client order id format. Needs TZ_PAPER_ALLOW_ORDERS=1.
        /// </summary>
        [Test]
        public void PlacesAndCancelsFarLimitOrder()
        {
            if (Environment.GetEnvironmentVariable("TZ_PAPER_ALLOW_ORDERS") != "1")
            {
                Assert.Ignore("Set TZ_PAPER_ALLOW_ORDERS=1 to place a paper order");
            }
            _brokerage.Connect();
            var events = new System.Collections.Generic.List<OrderEvent>();
            _brokerage.OrdersStatusChanged += (_, e) =>
            {
                lock (events)
                {
                    events.AddRange(e);
                }
                e.ForEach(_orders.HandleOrderEvent);
            };

            var order = new LimitOrder(TradeZeroHarness.Equity("AAPL"), 1, 1m, DateTime.UtcNow, properties: new OrderProperties { TimeInForce = TimeInForce.Day });
            _orders.Add(order);
            Assert.IsTrue(_brokerage.PlaceOrder(order), string.Join(" | ", events));
            System.Threading.Thread.Sleep(2000);
            _brokerage.PollOnce(force: true);
            Assert.AreEqual(OrderStatus.Submitted, _orders.GetOrderById(order.Id).Status, string.Join(" | ", events));

            Assert.IsTrue(_brokerage.CancelOrder(order));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (_orders.GetOrderById(order.Id).Status != OrderStatus.Canceled && DateTime.UtcNow < deadline)
            {
                System.Threading.Thread.Sleep(250);
            }
            Assert.AreEqual(OrderStatus.Canceled, _orders.GetOrderById(order.Id).Status, string.Join(" | ", events));
        }
    }
}
