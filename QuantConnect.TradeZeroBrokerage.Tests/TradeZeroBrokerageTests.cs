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
using NUnit.Framework;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using QuantConnect.Tests.Brokerages;

namespace QuantConnect.Brokerages.TradeZero.Tests
{
    /// <summary>
    /// Lean's standard brokerage test suite, run against tz-mock (basic_fills, AAPL around 250).
    /// Resting orders use prices far from the market; the "until filled" cases move them to the market
    /// with cancel-then-replace updates.
    /// </summary>
    [TestFixture, Category("TzMock"), NonParallelizable]
    public class TradeZeroBrokerageTests : BrokerageTests
    {
        private static readonly Symbol Aapl = TradeZeroHarness.Equity("AAPL");
        private string _key;

        protected override Symbol Symbol => Aapl;
        protected override SecurityType SecurityType => SecurityType.Equity;

        [OneTimeSetUp]
        public void StartMockSession()
        {
            TzMock.RequireAvailable();
            _key = TzMock.NewKey("lean-suite");
            TzMock.SetScenario(_key, "basic_fills");
        }

        protected override IBrokerage CreateBrokerage(IOrderProvider orderProvider, ISecurityProvider securityProvider)
        {
            var settings = new TradeZeroSettings
            {
                ApiUrl = TzMock.Url,
                ApiKeyId = _key,
                ApiSecretKey = "secret-" + _key,
                ActivePollInterval = TimeSpan.FromMilliseconds(300),
                IdlePollInterval = TimeSpan.FromSeconds(1),
            };
            return new TradeZeroBrokerage(settings, orderProvider, securityProvider);
        }

        // fills arrive through polling...
        protected override bool IsAsync() => true;

        // ...but a cancel is answered synchronously (DELETE returns the final order row), so CancelOrder
        // returns false for an order that is already filled
        protected override bool IsCancelAsync() => false;

        protected override decimal GetAskPrice(Symbol symbol) => TzMock.Price(_key, symbol.Value);

        private static IEnumerable<TestCaseData> OrderParameters()
        {
            yield return new TestCaseData(new MarketOrderTestParameters(Aapl)).SetName("Market");
            yield return new TestCaseData(new LimitOrderTestParameters(Aapl, 300m, 200m)).SetName("Limit");
            yield return new TestCaseData(new StopMarketOrderTestParameters(Aapl, 300m, 200m)).SetName("StopMarket");
            yield return new TestCaseData(new StopLimitOrderTestParameters(Aapl, 300m, 200m)).SetName("StopLimit");
        }

        [Test, TestCaseSource(nameof(OrderParameters))]
        public override void CancelOrders(OrderTestParameters parameters) => base.CancelOrders(parameters);

        [Test, TestCaseSource(nameof(OrderParameters))]
        public override void LongFromZero(OrderTestParameters parameters) => base.LongFromZero(parameters);

        [Test, TestCaseSource(nameof(OrderParameters))]
        public override void CloseFromLong(OrderTestParameters parameters) => base.CloseFromLong(parameters);

        [Test, TestCaseSource(nameof(OrderParameters))]
        public override void ShortFromZero(OrderTestParameters parameters) => base.ShortFromZero(parameters);

        [Test, TestCaseSource(nameof(OrderParameters))]
        public override void CloseFromShort(OrderTestParameters parameters) => base.CloseFromShort(parameters);

        [Test, TestCaseSource(nameof(OrderParameters))]
        public override void ShortFromLong(OrderTestParameters parameters) => base.ShortFromLong(parameters);

        [Test, TestCaseSource(nameof(OrderParameters))]
        public override void LongFromShort(OrderTestParameters parameters) => base.LongFromShort(parameters);

        [Test]
        public void LimitLongFromZeroUpdateAndCancel()
        {
            base.LongFromZeroUpdateAndCancel(new LimitOrderTestParameters(Aapl, 300m, 200m));
        }

        [Test]
        public override void GetAccountHoldings() => base.GetAccountHoldings();

        [Test]
        public override void GetCashBalanceContainsSomething() => base.GetCashBalanceContainsSomething();
    }
}
