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
using System.Linq;
using System.Threading;
using NUnit.Framework;
using QuantConnect.Orders;
using QuantConnect.Tests.Brokerages;

namespace QuantConnect.Brokerages.TradeZero.Tests
{
    /// <summary>
    /// A TradeZero brokerage connected to a tz-mock session, with Lean-like order and holdings bookkeeping
    /// (what the transaction handler does in a real algorithm)
    /// </summary>
    public sealed class TradeZeroHarness : IDisposable
    {
        private readonly List<OrderEvent> _events = new();
        private readonly List<BrokerageMessageEvent> _messages = new();

        public string Key { get; }
        public OrderProvider Orders { get; } = new();
        public SecurityProvider Securities { get; } = new();
        public TradeZeroBrokerage Brokerage { get; }
        public TradeZeroSettings Settings { get; }

        public TradeZeroHarness(string scenario = "basic_fills", object scenarioParameters = null, string key = null,
            TradeZeroEnvironment environment = TradeZeroEnvironment.Paper, string apiLogFile = null, string secret = null, bool connect = true)
        {
            TzMock.RequireAvailable();
            Key = key ?? TzMock.NewKey();
            if (scenario != null)
            {
                TzMock.SetScenario(Key, scenario, scenarioParameters);
            }
            Settings = new TradeZeroSettings
            {
                ApiUrl = TzMock.Url,
                ApiKeyId = Key,
                ApiSecretKey = secret ?? "secret-" + Key,
                Environment = environment,
                ApiLogFile = apiLogFile,
                ActivePollInterval = TimeSpan.FromMilliseconds(300),
                IdlePollInterval = TimeSpan.FromSeconds(1),
                AccountPollInterval = TimeSpan.FromSeconds(1),
                LocateTimeout = TimeSpan.FromSeconds(5),
                LocatePollInterval = TimeSpan.FromMilliseconds(100),
            };
            Brokerage = new TradeZeroBrokerage(Settings, Orders, Securities);
            Brokerage.OrdersStatusChanged += OnOrderEvents;
            Brokerage.Message += (_, message) =>
            {
                lock (_messages)
                {
                    _messages.Add(message);
                }
            };
            if (connect)
            {
                Brokerage.Connect();
                foreach (var holding in Brokerage.GetAccountHoldings())
                {
                    Securities.GetSecurity(holding.Symbol).Holdings.SetHoldings(holding.AveragePrice, holding.Quantity);
                }
            }
        }

        public static Symbol Equity(string ticker)
        {
            TestSetup.EnsureInitialized();
            return Symbol.Create(ticker, SecurityType.Equity, Market.USA);
        }

        public IReadOnlyList<OrderEvent> Events
        {
            get
            {
                Sync();
                lock (_events)
                {
                    return _events.ToList();
                }
            }
        }

        public IReadOnlyList<BrokerageMessageEvent> Messages
        {
            get
            {
                Sync();
                lock (_messages)
                {
                    return _messages.ToList();
                }
            }
        }

        public List<OrderEvent> EventsFor(Order order) => Events.Where(e => e.OrderId == order.Id).ToList();

        /// <summary>
        /// Registers the order with the order provider (assigns its id) and places it
        /// </summary>
        public bool Place(Order order)
        {
            Orders.Add(order);
            return Brokerage.PlaceOrder(order);
        }

        /// <summary>
        /// Applies an update request to the order, like Lean's transaction handler, then sends it to the brokerage
        /// </summary>
        public bool Update(Order order, UpdateOrderFields fields)
        {
            var request = new UpdateOrderRequest(DateTime.UtcNow, order.Id, fields);
            order.ApplyUpdateOrderRequest(request);
            return Brokerage.UpdateOrder(order);
        }

        public decimal Holdings(string ticker)
        {
            Sync();
            return Securities.GetSecurity(Equity(ticker)).Holdings.Quantity;
        }

        public OrderStatus StatusOf(Order order)
        {
            Sync();
            return Orders.GetOrderById(order.Id).Status;
        }

        /// <summary>
        /// Waits until the brokerage has delivered every callback raised so far (it delivers them on its own thread)
        /// </summary>
        public void Sync()
        {
            Assert.IsTrue(Brokerage.WaitForCallbacks(TimeSpan.FromSeconds(10)), "the brokerage's callbacks were not delivered within 10 s");
        }

        public decimal FilledQuantity(Order order) => EventsFor(order).Sum(e => e.FillQuantity);

        /// <summary>
        /// Waits until the order reaches the status (fails the test on timeout)
        /// </summary>
        public void WaitForStatus(Order order, OrderStatus status, double seconds = 10)
        {
            WaitFor(() => StatusOf(order) == status, $"order {order.Id} to be {status} (is {StatusOf(order)}; events: {string.Join(" | ", EventsFor(order))})", seconds);
        }

        public void WaitFor(Func<bool> condition, string what, double seconds = 10)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.Elapsed.TotalSeconds > seconds)
                {
                    Assert.Fail($"Timed out after {seconds}s waiting for {what}");
                }
                Thread.Sleep(50);
            }
        }

        /// <summary>
        /// Moves the mock clock (bars in between are replayed against working orders) and polls at once
        /// </summary>
        public void Advance(TimeSpan delta)
        {
            TzMock.AdvanceBy(Key, delta);
            Brokerage.PollOnce(force: true);
        }

        public void AdvanceTo(DateTime utc)
        {
            TzMock.AdvanceTo(Key, utc);
            Brokerage.PollOnce(force: true);
        }

        private void OnOrderEvents(object sender, List<OrderEvent> orderEvents)
        {
            foreach (var orderEvent in orderEvents)
            {
                lock (_events)
                {
                    _events.Add(orderEvent);
                }
                Orders.HandleOrderEvent(orderEvent);
                if (orderEvent.Status == OrderStatus.Filled || orderEvent.Status == OrderStatus.PartiallyFilled)
                {
                    var holding = Securities.GetSecurity(orderEvent.Symbol).Holdings;
                    holding.SetHoldings(orderEvent.FillPrice, holding.Quantity + orderEvent.FillQuantity);
                }
            }
        }

        public void Dispose()
        {
            Brokerage.Dispose();
        }
    }
}
