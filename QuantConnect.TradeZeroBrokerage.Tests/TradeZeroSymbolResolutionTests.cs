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
using System.Linq;
using NUnit.Framework;
using QuantConnect.Tests.Brokerages;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Brokerages.TradeZero.Tests
{
    /// <summary>
    /// TradeZero tickers to Lean symbols, for securities whose identifier is not their current ticker
    /// (ITP, first listed as ONP: "ONP UIJ2Z3ZD225H" in Lean)
    /// </summary>
    [TestFixture]
    public class TradeZeroSymbolResolutionTests
    {
        private static Symbol Renamed(string firstTicker, DateTime firstDate, string ticker)
            => new(SecurityIdentifier.GenerateEquity(firstDate, firstTicker, Market.USA), ticker);

        private static Symbol Unmapped(string ticker)
            => new(SecurityIdentifier.GenerateEquity(SecurityIdentifier.DefaultDate, ticker, Market.USA), ticker);

        [OneTimeSetUp]
        public void SetUp() => TestSetup.EnsureInitialized();

        [Test]
        public void KnownSecurityUnderTheTickerWinsOverTheBuiltSymbol()
        {
            var itp = Renamed("ONP", new DateTime(2009, 12, 17), "ITP");
            var other = Renamed("ABC", new DateTime(2001, 1, 2), "ABC");
            Assert.AreEqual(itp, TradeZeroBrokerage.MatchKnownSymbol(Unmapped("ITP"), new[] { other, itp }));
        }

        [Test]
        public void BuiltSymbolKeptWhenKnownItselfOrWithoutASingleMatch()
        {
            var built = Unmapped("ITP");
            var renamed = Renamed("ONP", new DateTime(2009, 12, 17), "ITP");
            Assert.AreSame(built, TradeZeroBrokerage.MatchKnownSymbol(built, Array.Empty<Symbol>()));
            Assert.AreEqual(built, TradeZeroBrokerage.MatchKnownSymbol(built, new[] { renamed, built }));
            var another = Renamed("XYZ", new DateTime(2015, 3, 4), "ITP");
            Assert.AreSame(built, TradeZeroBrokerage.MatchKnownSymbol(built, new[] { renamed, another }));
        }

        [Test]
        public void UnmappedMeansNoFirstListingDate()
        {
            Assert.IsTrue(TradeZeroBrokerage.IsUnmapped(Unmapped("ITP")));
            Assert.IsFalse(TradeZeroBrokerage.IsUnmapped(Renamed("ONP", new DateTime(2009, 12, 17), "ITP")));
            // Lean's sample map files know AAPL
            Assert.IsFalse(TradeZeroBrokerage.IsUnmapped(Symbol.Create("AAPL", SecurityType.Equity, Market.USA)));
        }

        [Test]
        public void QuantitiesAddUpByCurrentTicker()
        {
            var itp = Renamed("ONP", new DateTime(2009, 12, 17), "ITP");
            var quantities = TradeZeroBrokerage.QuantitiesByTicker(new[]
            {
                (itp, -3600m),
                (Unmapped("abc"), 5m),
                (Unmapped("ABC"), 2m),
                (Unmapped("FLAT"), 0m),
            });
            CollectionAssert.AreEquivalent(new Dictionary<string, decimal> { ["ITP"] = -3600m, ["ABC"] = 7m }, quantities);
        }

        [Test]
        public void ResolvesToTheAlgorithmsSecurityAndReportsUnmappedTickersOnce()
        {
            var algorithm = new AlgorithmStub();
            var itp = Renamed("ONP", new DateTime(2009, 12, 17), "ITP");
            algorithm.AddSecurity(itp);
            algorithm.Portfolio[itp].SetHoldings(3.1m, -3600);

            var settings = new TradeZeroSettings { ApiKeyId = "key", ApiSecretKey = "secret" };
            using var brokerage = new TradeZeroBrokerage(settings, new OrderProvider(), algorithm.Portfolio);
            var messages = new List<BrokerageMessageEvent>();
            brokerage.Message += (_, message) => messages.Add(message);

            Assert.AreEqual(itp, brokerage.ResolveLeanSymbol("ITP"));
            Assert.AreEqual(itp, brokerage.ResolveLeanSymbol("itp"));
            Assert.IsEmpty(messages);

            var unknown = brokerage.ResolveLeanSymbol("ZZZQX");
            Assert.IsTrue(TradeZeroBrokerage.IsUnmapped(unknown));
            brokerage.ResolveLeanSymbol("ZZZQX");
            var warning = messages.Single();
            Assert.AreEqual("UnmappedSymbol", warning.Code);
            StringAssert.Contains("ZZZQX", warning.Message);
        }
    }
}
