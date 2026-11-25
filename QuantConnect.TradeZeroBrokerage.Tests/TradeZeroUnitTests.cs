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
using NUnit.Framework;
using QuantConnect.Brokerages.TradeZero.Api;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Tests.Brokerages;

namespace QuantConnect.Brokerages.TradeZero.Tests
{
    [TestFixture]
    public class TradeZeroUnitTests
    {
        private static readonly Symbol Aapl = TradeZeroHarness.Equity("AAPL");

        // ---------------------------------------------------------------- order statuses

        [TestCase(TzOrderStatus.Filled, true)]
        [TestCase(TzOrderStatus.Canceled, true)]
        [TestCase(TzOrderStatus.Rejected, true)]
        [TestCase(TzOrderStatus.Expired, true)]
        [TestCase(TzOrderStatus.DoneForDay, false)] // parked until the next session, then New again
        [TestCase(TzOrderStatus.New, false)]
        [TestCase(TzOrderStatus.PartiallyFilled, false)]
        public void TerminalStatuses(string status, bool terminal)
        {
            Assert.AreEqual(terminal, TzOrderStatus.IsTerminal(status));
        }

        // ---------------------------------------------------------------- paper / live guard

        [TestCase(TradeZeroEnvironment.Paper, "Paper", true)]
        [TestCase(TradeZeroEnvironment.Paper, "Live", false)]
        [TestCase(TradeZeroEnvironment.Paper, "Margin", false)]
        [TestCase(TradeZeroEnvironment.Paper, null, false)]
        [TestCase(TradeZeroEnvironment.Live, "Margin", true)]
        [TestCase(TradeZeroEnvironment.Live, "Paper", false)]
        public void EnvironmentGuard(TradeZeroEnvironment environment, string accountType, bool allowed)
        {
            if (allowed)
            {
                Assert.DoesNotThrow(() => TradeZeroBrokerage.EnsureEnvironment(environment, "TZP123", accountType));
            }
            else
            {
                var e = Assert.Throws<InvalidOperationException>(() => TradeZeroBrokerage.EnsureEnvironment(environment, "TZP123", accountType));
                StringAssert.StartsWith("Refusing to connect", e.Message);
            }
        }

        [Test]
        public void EnvironmentDefaultsToPaper()
        {
            Assert.AreEqual(TradeZeroEnvironment.Paper, TradeZeroSettings.ParseEnvironment(null));
            Assert.AreEqual(TradeZeroEnvironment.Paper, TradeZeroSettings.ParseEnvironment(""));
            Assert.AreEqual(TradeZeroEnvironment.Live, TradeZeroSettings.ParseEnvironment("LIVE"));
            Assert.Throws<ArgumentException>(() => TradeZeroSettings.ParseEnvironment("prod"));
        }

        [Test]
        public void SelectsAccount()
        {
            var one = new List<TzAccount> { new() { Account = "TZP1" } };
            Assert.AreEqual("TZP1", TradeZeroBrokerage.SelectAccount(one, null).Account);
            var two = new List<TzAccount> { new() { Account = "TZP1" }, new() { Account = "TZP2" } };
            Assert.AreEqual("TZP2", TradeZeroBrokerage.SelectAccount(two, "tzp2").Account);
            Assert.Throws<InvalidOperationException>(() => TradeZeroBrokerage.SelectAccount(two, null));
            Assert.Throws<InvalidOperationException>(() => TradeZeroBrokerage.SelectAccount(two, "TZP3"));
            Assert.Throws<InvalidOperationException>(() => TradeZeroBrokerage.SelectAccount(new List<TzAccount>(), null));
        }

        // ---------------------------------------------------------------- settings

        [Test]
        public void SettingsFromConfigWithEnvironmentFallbackForKeys()
        {
            var data = new Dictionary<string, string>
            {
                [TradeZeroSettings.ApiUrlKey] = "http://tzmock:8000",
                [TradeZeroSettings.AccountIdKey] = "TZP1",
                [TradeZeroSettings.ActivePollMsKey] = "750",
                [TradeZeroSettings.FeePerOrderKey] = "0.99",
                [TradeZeroSettings.ApiLogFileKey] = "/tmp/tz.jsonl",
            };
            var env = new Dictionary<string, string> { ["TZ_API_KEY_ID"] = "id-from-env", ["TZ_API_SECRET_KEY"] = "secret-from-env" };
            var settings = TradeZeroSettings.FromBrokerageData(data, name => env.TryGetValue(name, out var v) ? v : null);

            Assert.AreEqual("http://tzmock:8000", settings.ApiUrl);
            Assert.AreEqual("id-from-env", settings.ApiKeyId);
            Assert.AreEqual("secret-from-env", settings.ApiSecretKey);
            Assert.AreEqual(TradeZeroEnvironment.Paper, settings.Environment);
            Assert.AreEqual(TimeSpan.FromMilliseconds(750), settings.ActivePollInterval);
            Assert.AreEqual(0.99m, settings.FeePerOrder);
            Assert.AreEqual("/tmp/tz.jsonl", settings.ApiLogFile);

            data[TradeZeroSettings.ApiKeyIdKey] = "id-from-config";
            Assert.AreEqual("id-from-config", TradeZeroSettings.FromBrokerageData(data, _ => "ignored").ApiKeyId);
        }

        [Test]
        public void FactoryRefusesMissingKeys()
        {
            using var factory = new TradeZeroBrokerageFactory();
            var job = new QuantConnect.Packets.LiveNodePacket { BrokerageData = new Dictionary<string, string>() };
            if (Environment.GetEnvironmentVariable("TZ_API_KEY_ID") == null)
            {
                var e = Assert.Throws<ArgumentException>(() => factory.CreateBrokerage(job, null));
                StringAssert.Contains(TradeZeroSettings.ApiKeyIdKey, e.Message);
            }
            Assert.IsInstanceOf<TradeZeroBrokerageModel>(factory.GetBrokerageModel(null));
        }

        // ---------------------------------------------------------------- cash and holdings

        [Test]
        public void LeanCashCountsShortProceeds()
        {
            var pnl = new TzPnl
            {
                AccountValue = 100_000m,
                Rows = new List<TzPnlRow>
                {
                    new() { PositionId = "1", Symbol = "AAPL", Exposure = 2_500m },   // 10 long @ 250
                    new() { PositionId = "2", Symbol = "MSFT", Exposure = -4_000m },  // 10 short @ 400
                    new() { PositionId = "3", Symbol = "IBM", Exposure = 0m },        // closed lot
                }
            };
            var positions = new List<TzPosition>
            {
                new() { PositionId = "1", Symbol = "AAPL", Shares = 10, Side = "Long", PriceAvg = 240m },
                new() { PositionId = "2", Symbol = "MSFT", Shares = 10, Side = "Short", PriceAvg = 410m },
                new() { PositionId = "3", Symbol = "IBM", Shares = 0, Side = "Long", PriceAvg = 300m },
            };
            // 100000 - (10*250 - 10*400) = 101500
            Assert.AreEqual(101_500m, TradeZeroBrokerage.ComputeLeanCash(pnl, positions));
            Assert.AreEqual(-10m, positions[1].SignedShares);
            Assert.AreEqual(400m, TradeZeroBrokerage.MarkPrice(positions[1], pnl));
        }

        [Test]
        public void SignedSharesAcceptsBothConventions()
        {
            Assert.AreEqual(-5m, new TzPosition { Shares = 5, Side = "Short" }.SignedShares);
            Assert.AreEqual(-5m, new TzPosition { Shares = -5, Side = "Short" }.SignedShares);
            Assert.AreEqual(5m, new TzPosition { Shares = 5, Side = "Long" }.SignedShares);
        }

        // ---------------------------------------------------------------- symbols

        [TestCase("AAPL")]
        [TestCase("BRK.B")]
        public void SymbolRoundTrip(string ticker)
        {
            var mapper = new TradeZeroSymbolMapper();
            var symbol = mapper.GetLeanSymbol(ticker);
            Assert.AreEqual(SecurityType.Equity, symbol.SecurityType);
            Assert.AreEqual(ticker, mapper.GetBrokerageSymbol(symbol));
        }

        [Test]
        public void SymbolMapperRejectsNonEquities()
        {
            var mapper = new TradeZeroSymbolMapper();
            Assert.Throws<ArgumentException>(() => mapper.GetBrokerageSymbol(Symbol.Create("EURUSD", SecurityType.Forex, Market.Oanda)));
            Assert.Throws<ArgumentException>(() => mapper.GetLeanSymbol("AAPL", SecurityType.Option, Market.USA));
        }

        // ---------------------------------------------------------------- brokerage model

        private static Security NewSecurity() => new SecurityProvider().GetSecurity(Aapl);

        [Test]
        public void ModelAcceptsEquityOrdersOnly()
        {
            var model = new TradeZeroBrokerageModel();
            var security = NewSecurity();
            Assert.IsTrue(model.CanSubmitOrder(security, new LimitOrder(security.Symbol, 10, 100m, DateTime.UtcNow), out _));
            Assert.IsFalse(model.CanSubmitOrder(security, new MarketOrder(security.Symbol, 0.5m, DateTime.UtcNow), out var message));
            StringAssert.Contains("fractional", message.Message);
            Assert.IsFalse(model.CanSubmitOrder(security, new TrailingStopOrder(security.Symbol, 10, 90m, 1m, false, DateTime.UtcNow), out _));
        }

        [Test]
        public void ModelUpdatesOnlyRestingOrders()
        {
            var model = new TradeZeroBrokerageModel();
            var security = NewSecurity();
            var request = new UpdateOrderRequest(DateTime.UtcNow, 1, new UpdateOrderFields { LimitPrice = 101m });
            Assert.IsTrue(model.CanUpdateOrder(security, new LimitOrder(security.Symbol, 10, 100m, DateTime.UtcNow), request, out _));
            Assert.IsFalse(model.CanUpdateOrder(security, new MarketOnOpenOrder(security.Symbol, 10, DateTime.UtcNow), request, out _));
            var fractional = new UpdateOrderRequest(DateTime.UtcNow, 1, new UpdateOrderFields { Quantity = 10.5m });
            Assert.IsFalse(model.CanUpdateOrder(security, new LimitOrder(security.Symbol, 10, 100m, DateTime.UtcNow), fractional, out _));
        }

        [Test]
        public void ModelUsesPatternDayTradingMarginForMarginAccounts()
        {
            var security = NewSecurity();
            Assert.IsInstanceOf<PatternDayTradingMarginModel>(new TradeZeroBrokerageModel().GetBuyingPowerModel(security));
            Assert.AreEqual(2m, new TradeZeroBrokerageModel().GetLeverage(security));
            Assert.AreEqual(1m, new TradeZeroBrokerageModel(AccountType.Cash).GetLeverage(security));
            Assert.IsInstanceOf<TradeZeroShortableProvider>(new TradeZeroBrokerageModel().GetShortableProvider(security));
        }

        [Test]
        public void ShortableProviderWithoutConnectionAllowsShorting()
        {
            Assert.IsNull(TradeZeroShortableProvider.Instance.ShortableQuantity(Aapl, DateTime.UtcNow));
        }

        [Test]
        public void FeeModel()
        {
            var fees = new TradeZeroFeeModel(perOrder: 0.99m, perShare: 0.005m);
            Assert.AreEqual(0.99m + 0.5m, fees.GetFillFee(100, isFirstFill: true).Value.Amount);
            Assert.AreEqual(0.5m, fees.GetFillFee(100, isFirstFill: false).Value.Amount);
            Assert.AreEqual(0m, new TradeZeroFeeModel().GetFillFee(100, true).Value.Amount);
        }
    }
}
