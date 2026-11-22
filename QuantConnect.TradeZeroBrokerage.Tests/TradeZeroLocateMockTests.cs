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
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.TradeZero.Tests
{
    /// <summary>
    /// Hard-to-borrow shorts with locates, and the <see cref="TradeZeroLocates"/> API, against tz-mock's short_htb scenario:
    /// HTBX locates at $0.03, NOLOC no supply, SUBP under $0.50, REGSHO PreBorrow $0.03 / SingleUse $0.01 (see tz-mock/README.md)
    /// </summary>
    [TestFixture, Category("TzMock"), NonParallelizable]
    public class TradeZeroLocateMockTests
    {
        private static readonly Symbol Htbx = TradeZeroHarness.Equity("HTBX");
        private static readonly Symbol Regsho = TradeZeroHarness.Equity("REGSHO");
        private static DateTime Now => DateTime.UtcNow;

        private static TradeZeroOrderProperties Props(Func<TradeZeroLocateQuote, TradeZeroLocateOffer> handler = null, bool suppressOddLot = false)
        {
            return new TradeZeroOrderProperties { LocateHandler = handler, SuppressOddLotLocateWarning = suppressOddLot };
        }

        private static MarketOrder Short(Symbol symbol, decimal quantity, TradeZeroOrderProperties properties = null)
        {
            return new MarketOrder(symbol, -Math.Abs(quantity), Now, properties: properties ?? Props());
        }

        /// <summary>A handler that records the quote and picks an offer (null: decline)</summary>
        private sealed class Recorder
        {
            private readonly Func<TradeZeroLocateQuote, TradeZeroLocateOffer> _choose;
            public readonly List<TradeZeroLocateQuote> Quotes = new();
            public Recorder(Func<TradeZeroLocateQuote, TradeZeroLocateOffer> choose) => _choose = choose;
            public TradeZeroLocateQuote Quote => Quotes.Single();

            public TradeZeroLocateOffer Handle(TradeZeroLocateQuote quote)
            {
                lock (Quotes)
                {
                    Quotes.Add(quote);
                }
                return _choose(quote);
            }
        }

        private static int StatusOf(string quoteReqId) => TradeZeroLocates.GetHistory().Single(r => r.QuoteReqId == quoteReqId).Status;

        private static decimal LocateCosts(TradeZeroHarness h) => TzMock.Locates(h.Key)["locateCosts"]!.Value<decimal>();

        // ------------------------------------------------------------------ no handler

        [Test]
        public void NoHandlerAndNoInventoryIsRefusedWithoutAQuote()
        {
            using var h = new TradeZeroHarness("short_htb");
            var order = Short(Htbx, 100);
            Assert.IsFalse(h.Place(order));
            var invalid = h.EventsFor(order).Single();
            Assert.AreEqual(OrderStatus.Invalid, invalid.Status);
            StringAssert.Contains("needs 100 more located shares (0 held)", invalid.Message);
            StringAssert.Contains("LocateHandler", invalid.Message);
            Assert.IsEmpty(TradeZeroLocates.GetHistory());
            Assert.IsEmpty(TzMock.State(h.Key)["workingOrders"]!);
        }

        [Test]
        public void NoHandlerButInventoryCoversTheShort()
        {
            using var h = new TradeZeroHarness("short_htb", new { locate_inventory = new { HTBX = 200 } });
            var order = Short(Htbx, 100);
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Filled);
            Assert.AreEqual(-100m, h.Holdings("HTBX"));

            var inventory = TradeZeroLocates.GetInventory(Htbx).Single();
            Assert.AreEqual(TradeZeroLocateType.Locate, inventory.Type);
            Assert.AreEqual(100, inventory.Available);
            Assert.AreEqual(100, inventory.Unavailable);
            Assert.AreEqual(100, TradeZeroLocates.GetAvailable(Htbx));
            Assert.IsEmpty(TradeZeroLocates.GetHistory());
        }

        [Test]
        public void NoHandlerAndPartialInventoryIsRefused()
        {
            using var h = new TradeZeroHarness("short_htb", new { locate_inventory = new { HTBX = 100 } });
            var order = Short(Htbx, 200);
            Assert.IsFalse(h.Place(order));
            StringAssert.Contains("needs 100 more located shares (100 held)", h.EventsFor(order).Single().Message);
        }

        // ------------------------------------------------------------------ handler: ordinary locate

        [Test]
        public void HandlerAcceptsTheOfferAndTheCostGoesOnTheFirstFill()
        {
            using var h = new TradeZeroHarness("short_htb");
            var handler = new Recorder(q => q.Offers[0]);
            var order = Short(Htbx, 200, Props(handler.Handle));
            Assert.IsTrue(h.Place(order));
            Assert.AreEqual(OrderStatus.Submitted, h.EventsFor(order).First().Status);
            h.WaitForStatus(order, OrderStatus.Filled);

            var quote = handler.Quote;
            Assert.AreEqual(Htbx, quote.Symbol);
            Assert.AreEqual(order.Id, quote.LeanOrderId);
            Assert.AreEqual(-200m, quote.OrderQuantity);
            Assert.AreEqual(200, quote.ShortQuantity);
            Assert.AreEqual(0, quote.AlreadyLocated);
            Assert.AreEqual(200, quote.RequestedQuantity);
            var offer = quote.Offers.Single();
            Assert.AreEqual(TradeZeroLocateType.Locate, offer.Type);
            Assert.AreEqual(quote.QuoteReqId, offer.QuoteReqId);
            Assert.AreEqual(200, offer.Quantity);
            Assert.AreEqual(0.03m, offer.PricePerShare);
            Assert.AreEqual(6m, offer.TotalCost);
            Assert.IsTrue(offer.CoversOrder);
            Assert.IsTrue(offer.ReusableAfterCover);
            Assert.IsTrue(offer.SellBackAfterUse);

            var fill = h.EventsFor(order).Single(e => e.Status == OrderStatus.Filled);
            Assert.AreEqual(6m, fill.OrderFee.Value.Amount);
            Assert.AreEqual(6m, LocateCosts(h));
            Assert.AreEqual(TzLocateStatusFilled, StatusOf(quote.QuoteReqId));
            Assert.IsTrue(h.Messages.Any(m => m.Code == "LocatePurchased"));
            Assert.AreEqual(-200m, h.Holdings("HTBX"));
        }

        private const int TzLocateStatusFilled = 50;

        [Test]
        public void HandlerDeclinesAndTheQuoteIsCanceled()
        {
            using var h = new TradeZeroHarness("short_htb");
            var handler = new Recorder(_ => null);
            var order = Short(Htbx, 100, Props(handler.Handle));
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Invalid);
            StringAssert.Contains("locate declined", h.EventsFor(order).Last().Message);
            Assert.AreEqual(52, StatusOf(handler.Quote.QuoteReqId));
            Assert.IsTrue(h.Messages.Any(m => m.Code == "LocateDeclined"));
            Assert.AreEqual(0m, LocateCosts(h));
            Assert.IsEmpty(TzMock.State(h.Key)["workingOrders"]!);
        }

        [Test]
        public void HandlerThatThrowsCountsAsDecline()
        {
            using var h = new TradeZeroHarness("short_htb");
            var order = Short(Htbx, 100, Props(_ => throw new InvalidOperationException("boom")));
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Invalid);
            StringAssert.Contains("boom", h.EventsFor(order).Last().Message);
            Assert.IsTrue(h.Messages.Any(m => m.Code == "LocateHandlerError"));
            Assert.IsEmpty(TzMock.Locates(h.Key)["openQuotes"]!);
        }

        [Test]
        public void QuoteThatTakesSeveralPollsStillCompletes()
        {
            using var h = new TradeZeroHarness("short_htb", new { locate_quote_polls = 3 });
            var order = Short(Htbx, 100, Props(q => q.Offers[0]));
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Filled);
        }

        [TestCase("NOLOC", "11 No shares available")]
        [TestCase("SUBP", "R40")]
        public void BrokerRefusalEndsTheOrderInvalidWithTheBrokerText(string ticker, string text)
        {
            using var h = new TradeZeroHarness("short_htb");
            var called = false;
            var order = Short(TradeZeroHarness.Equity(ticker), 100, Props(q => { called = true; return q.Offers[0]; }));
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Invalid);
            StringAssert.Contains(text, h.EventsFor(order).Last().Message);
            Assert.IsFalse(called, "the handler is only called with offers");
            Assert.IsTrue(h.Messages.Any(m => m.Code == "LocateRefused" && m.Message.Contains(text)));
        }

        [Test]
        public void LocateAlreadyPendingIsReported()
        {
            using var h = new TradeZeroHarness("short_htb");
            TzMock.NextLocateError(h.Key, "HTBX", 14, "14 Locate is already pending.");
            var order = Short(Htbx, 100, Props(q => q.Offers[0]));
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Invalid);
            StringAssert.Contains("14 Locate is already pending.", h.EventsFor(order).Last().Message);
        }

        [Test]
        public void SymbolThatBecameEasyToBorrowIsSentToTheBroker()
        {
            using var h = new TradeZeroHarness("short_htb");
            TzMock.NextLocateError(h.Key, "HTBX", 56, "R56: This symbol is Easy to Borrow.");
            var order = Short(Htbx, 100, Props(q => q.Offers[0]));
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Invalid);
            // the mock still treats HTBX as hard to borrow for orders: the broker's own reject proves the order was sent
            StringAssert.Contains("TradeZero rejected the order", h.EventsFor(order).Last().Message);
            Assert.AreEqual(1, TzMock.State(h.Key)["terminalOrders"]!.Value<int>());
        }

        [Test]
        public void OfferThatExpiresWhileTheHandlerRunsEndsInvalid()
        {
            using var h = new TradeZeroHarness("short_htb");
            var order = Short(Htbx, 100, Props(q =>
            {
                TzMock.AdvanceBy(h.Key, TimeSpan.FromSeconds(31));
                return q.Offers[0];
            }));
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Invalid);
            StringAssert.Contains("expired", h.EventsFor(order).Last().Message);
            Assert.AreEqual(0m, LocateCosts(h));
        }

        [Test]
        public void AcceptThatIsNeverAppliedEndsInvalidAfterTheTimeout()
        {
            using var h = new TradeZeroHarness("short_htb", new { locate_accept_ignored = new[] { "HTBX" } });
            h.Settings.LocateTimeout = TimeSpan.FromSeconds(1);
            var order = Short(Htbx, 100, Props(q => q.Offers[0]));
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Invalid);
            StringAssert.Contains("not filled within", h.EventsFor(order).Last().Message);
            Assert.IsEmpty(TzMock.Locates(h.Key)["openQuotes"]!, "the unfilled offer is canceled");
        }

        [Test]
        public void LostAcceptResponseIsResentOnceAndChargedOnce()
        {
            using var h = new TradeZeroHarness("short_htb");
            TzMock.AddFault(h.Key, "POST", "*/locates/accept", 500, when: "after");
            var order = Short(Htbx, 100, Props(q => q.Offers[0]));
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Filled);
            Assert.AreEqual(3m, LocateCosts(h));
        }

        // ------------------------------------------------------------------ Reg SHO: PreBorrow and SingleUse

        [Test]
        public void RegShoQuoteOffersBothTypesCheapestFirst()
        {
            using var h = new TradeZeroHarness("short_htb");
            var handler = new Recorder(q => q.Offers.Single(o => o.Type == TradeZeroLocateType.PreBorrow));
            var order = Short(Regsho, 100, Props(handler.Handle));
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Filled);

            var quote = handler.Quote;
            Assert.AreEqual(2, quote.Offers.Count);
            var singleUse = quote.Offers[0];
            var preBorrow = quote.Offers[1];
            Assert.AreEqual(TradeZeroLocateType.SingleUse, singleUse.Type);
            Assert.AreEqual(quote.QuoteReqId + ".SU", singleUse.QuoteReqId);
            Assert.AreEqual(0.01m, singleUse.PricePerShare);
            Assert.IsFalse(singleUse.ReusableAfterCover);
            Assert.IsFalse(singleUse.SellBackAfterUse);
            Assert.AreEqual(TradeZeroLocateType.PreBorrow, preBorrow.Type);
            Assert.AreEqual(quote.QuoteReqId, preBorrow.QuoteReqId);
            Assert.AreEqual(0.03m, preBorrow.PricePerShare);
            Assert.IsTrue(preBorrow.ReusableAfterCover);

            // PreBorrow accepted on the base id; the SingleUse row expired by itself
            Assert.AreEqual(50, StatusOf(quote.QuoteReqId));
            Assert.AreEqual(67, StatusOf(quote.QuoteReqId + ".SU"));
            Assert.AreEqual(TradeZeroLocateType.PreBorrow, TradeZeroLocates.GetInventory(Regsho).Single().Type);
            Assert.AreEqual(3m, h.EventsFor(order).Single(e => e.Status == OrderStatus.Filled).OrderFee.Value.Amount);
        }

        [Test]
        public void RegShoSingleUseIsAcceptedWithTheSuId()
        {
            using var h = new TradeZeroHarness("short_htb");
            var handler = new Recorder(q => q.Offers.Single(o => o.Type == TradeZeroLocateType.SingleUse));
            var order = Short(Regsho, 100, Props(handler.Handle));
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Filled);
            Assert.AreEqual(50, StatusOf(handler.Quote.QuoteReqId + ".SU"));
            Assert.AreEqual(67, StatusOf(handler.Quote.QuoteReqId));
            Assert.AreEqual(TradeZeroLocateType.SingleUse, TradeZeroLocates.GetInventory(Regsho).Single().Type);
            Assert.AreEqual(1m, LocateCosts(h));
        }

        [Test]
        public void RegShoDeclineCancelsBothRows()
        {
            using var h = new TradeZeroHarness("short_htb");
            var handler = new Recorder(_ => null);
            var order = Short(Regsho, 100, Props(handler.Handle));
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Invalid);
            Assert.AreEqual(52, StatusOf(handler.Quote.QuoteReqId));
            Assert.AreEqual(52, StatusOf(handler.Quote.QuoteReqId + ".SU"));
        }

        [Test]
        public void RegShoWithOnlySingleUseOfferedGivesOneOfferAndARefusalMessage()
        {
            using var h = new TradeZeroHarness("short_htb", new { locate_price = new { REGSHO = new { SingleUse = 0.01 } } });
            var handler = new Recorder(q => q.Offers[0]);
            var order = Short(Regsho, 100, Props(handler.Handle));
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Filled);
            Assert.AreEqual(TradeZeroLocateType.SingleUse, handler.Quote.Offers.Single().Type);
            Assert.IsTrue(h.Messages.Any(m => m.Code == "LocateRefused" && m.Message.Contains("PreBorrow") && m.Message.Contains("No shares available")));
        }

        [Test]
        public void RegShoSiblingThatAppearsOnePollLaterIsNotMissed()
        {
            using var h = new TradeZeroHarness("short_htb", new { locate_sibling_polls = 1 });
            var handler = new Recorder(q => q.Offers[0]);
            var order = Short(Regsho, 100, Props(handler.Handle));
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Filled);
            Assert.AreEqual(2, handler.Quote.Offers.Count);
        }

        [Test]
        public void HandlerReturningAForeignObjectCountsAsDecline()
        {
            using var h = new TradeZeroHarness("short_htb");
            TradeZeroLocateOffer stale = null;
            var first = Short(Regsho, 100, Props(q => { stale = q.Offers[0]; return null; }));
            Assert.IsTrue(h.Place(first));
            h.WaitForStatus(first, OrderStatus.Invalid);

            var handler = new Recorder(_ => stale);
            var second = Short(Regsho, 100, Props(handler.Handle));
            Assert.IsTrue(h.Place(second));
            h.WaitForStatus(second, OrderStatus.Invalid);
            Assert.IsTrue(h.Messages.Any(m => m.Code == "LocateHandlerError" && m.Message.Contains("not one of quote.Offers")));
            Assert.AreEqual(52, StatusOf(handler.Quote.QuoteReqId));
            Assert.AreEqual(52, StatusOf(handler.Quote.QuoteReqId + ".SU"));
        }

        [TestCase(TradeZeroLocateType.SingleUse, false)]
        [TestCase(TradeZeroLocateType.PreBorrow, true)]
        public void OnlyReusableLocatesCoverASecondShortAfterACover(TradeZeroLocateType type, bool secondShortFills)
        {
            using var h = new TradeZeroHarness("short_htb");
            var first = Short(Regsho, 100, Props(q => q.Offers.Single(o => o.Type == type)));
            Assert.IsTrue(h.Place(first));
            h.WaitForStatus(first, OrderStatus.Filled);
            var cover = new MarketOrder(Regsho, 100, Now);
            Assert.IsTrue(h.Place(cover));
            h.WaitForStatus(cover, OrderStatus.Filled);

            var second = Short(Regsho, 100);
            Assert.AreEqual(secondShortFills, h.Place(second));
            if (secondShortFills)
            {
                h.WaitForStatus(second, OrderStatus.Filled);
                Assert.AreEqual(2, TradeZeroLocates.GetHistory().Count, "no new quote");
            }
            else
            {
                StringAssert.Contains("needs 100 more located shares (0 held)", h.EventsFor(second).Single().Message);
            }
        }

        [Test]
        public void LocatePoolCodeTwoIsAnOrdinaryLocate()
        {
            using var h = new TradeZeroHarness("short_htb", new { locate_pool_code = new { HTBX = 2 } });
            var handler = new Recorder(q => q.Offers[0]);
            var order = Short(Htbx, 100, Props(handler.Handle));
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Filled);
            Assert.AreEqual(TradeZeroLocateType.Locate, handler.Quote.Offers.Single().Type);
            Assert.AreEqual(TradeZeroLocateType.Locate, TradeZeroLocates.GetInventory(Htbx).Single().Type);
        }

        // ------------------------------------------------------------------ odd lots

        [TestCase(150, false, true)]
        [TestCase(150, true, false)]
        [TestCase(200, false, false)]
        public void OddLotWarning(int quantity, bool suppress, bool warned)
        {
            using var h = new TradeZeroHarness("short_htb", new { locate_inventory = new { HTBX = 200 } });
            var order = Short(Htbx, quantity, Props(suppressOddLot: suppress));
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Filled);
            var warnings = h.Messages.Where(m => m.Code == "LocateOddLot").ToList();
            Assert.AreEqual(warned ? 1 : 0, warnings.Count);
            if (warned)
            {
                StringAssert.Contains("50 located shares will stay unused", warnings[0].Message);
            }
        }

        [Test]
        public void OddLotShortRequestsARoundedLocate()
        {
            using var h = new TradeZeroHarness("short_htb", new { locate_inventory = new { HTBX = 100 } });
            var handler = new Recorder(q => q.Offers[0]);
            var order = Short(Htbx, 150, Props(handler.Handle));
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Filled);
            Assert.AreEqual(100, handler.Quote.AlreadyLocated);
            Assert.AreEqual(100, handler.Quote.RequestedQuantity, "need 50, rounded up to a lot of 100");
            Assert.AreEqual(50, TradeZeroLocates.GetAvailable(Htbx));
        }

        // ------------------------------------------------------------------ cancel / update while locating

        [Test]
        public void CancelWhileTheHandlerRunsStopsEverything()
        {
            using var h = new TradeZeroHarness("short_htb");
            using var inHandler = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var handler = new Recorder(q =>
            {
                inHandler.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                return q.Offers[0];
            });
            var order = Short(Htbx, 100, Props(handler.Handle));
            Assert.IsTrue(h.Place(order));
            Assert.IsTrue(inHandler.Wait(TimeSpan.FromSeconds(5)));

            Assert.IsFalse(h.Update(order, new UpdateOrderFields { Quantity = -200 }), "no update while waiting for a locate");
            Assert.IsTrue(h.Messages.Any(m => m.Code == "UpdateRejected" && m.Message.Contains("waiting for a locate")));

            Assert.IsTrue(h.Brokerage.CancelOrder(order));
            h.WaitForStatus(order, OrderStatus.Canceled);
            release.Set();
            h.WaitFor(() => TradeZeroLocates.GetHistory().Any(r => r.Status == 52), "the offer to be canceled");
            Thread.Sleep(300);
            Assert.AreEqual(0, TzMock.State(h.Key)["terminalOrders"]!.Value<int>());
            Assert.IsEmpty(TzMock.State(h.Key)["workingOrders"]!);
            Assert.AreEqual(0m, LocateCosts(h));
        }

        [Test]
        public void UpdateThatNeedsMoreLocatedSharesIsRefusedBeforeCanceling()
        {
            using var h = new TradeZeroHarness("short_htb", new { locate_inventory = new { HTBX = 100 } });
            var price = TzMock.Price(h.Key, "HTBX");
            var order = new LimitOrder(Htbx, -100, Math.Round(price * 2, 2), Now);
            Assert.IsTrue(h.Place(order));
            h.Brokerage.PollOnce(force: true);
            Assert.AreEqual(OrderStatus.Submitted, h.StatusOf(order));

            Assert.IsFalse(h.Update(order, new UpdateOrderFields { Quantity = -300 }));
            Assert.IsTrue(h.Messages.Any(m => m.Code == "UpdateRejected" && m.Message.Contains("needs 200 more located shares")));
            Assert.AreEqual(1, TzMock.State(h.Key)["workingOrders"]!.Count(), "the original order still works");

            // within the located shares (its own reservation counts): accepted
            Assert.IsTrue(h.Update(order, new UpdateOrderFields { Quantity = -100, LimitPrice = Math.Round(price * 3, 2) }));
        }

        // ------------------------------------------------------------------ zero crossing, concurrency

        [Test]
        public void ZeroCrossingOrderLocatesBeforeClosingTheLong()
        {
            using var h = new TradeZeroHarness("short_htb");
            var buy = new MarketOrder(Htbx, 50, Now);
            Assert.IsTrue(h.Place(buy));
            h.WaitForStatus(buy, OrderStatus.Filled);

            var handler = new Recorder(q => q.Offers[0]);
            var order = new MarketOrder(Htbx, -250, Now, properties: Props(handler.Handle));
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Filled);
            Assert.AreEqual(200, handler.Quote.ShortQuantity);
            Assert.AreEqual(200, handler.Quote.RequestedQuantity);
            Assert.AreEqual(-250m, handler.Quote.OrderQuantity);
            Assert.AreEqual(-200m, h.Holdings("HTBX"));
            Assert.AreEqual(2, order.BrokerId.Count);
        }

        [Test]
        public void ZeroCrossingOrderWhoseInventoryIsTakenBetweenTheLegsIsCanceled()
        {
            using var h = new TradeZeroHarness("short_htb", new { locate_inventory = new { HTBX = 200 } });
            var buy = new MarketOrder(Htbx, 50, Now);
            Assert.IsTrue(h.Place(buy));
            h.WaitForStatus(buy, OrderStatus.Filled);

            // HTBX starts near 8.00 and trades up to 8.09 within the hour: the closing leg rests, then fills
            var order = new LimitOrder(Htbx, -250, 8.05m, Now);
            Assert.IsTrue(h.Place(order));
            h.Brokerage.PollOnce(force: true);
            Assert.AreEqual(OrderStatus.Submitted, h.StatusOf(order));
            TzMock.SetInventory(h.Key, "HTBX", 0);

            h.Advance(TimeSpan.FromMinutes(60));
            h.WaitForStatus(order, OrderStatus.Canceled);
            StringAssert.Contains("short part was not placed", h.EventsFor(order).Last().Message);
            Assert.AreEqual(0m, h.Holdings("HTBX"), "the long was closed");
        }

        [Test]
        public void TwoShortsOfTheSameSymbolEachGetTheirOwnLocate()
        {
            using var h = new TradeZeroHarness("short_htb");
            var first = Short(Htbx, 100, Props(q => q.Offers[0]));
            var second = Short(Htbx, 100, Props(q => q.Offers[0]));
            Assert.IsTrue(h.Place(first));
            Assert.IsTrue(h.Place(second));
            h.WaitForStatus(first, OrderStatus.Filled);
            h.WaitForStatus(second, OrderStatus.Filled);
            Assert.AreEqual(2, TradeZeroLocates.GetHistory().Count(r => r.Status == 50));
            Assert.AreEqual(6m, LocateCosts(h));
            Assert.AreEqual(-200m, h.Holdings("HTBX"));
        }

        // ------------------------------------------------------------------ TradeZeroLocates API

        [Test]
        public void LocateWithoutAnOrderAddsToTheInventory()
        {
            using var h = new TradeZeroHarness("short_htb");
            var result = TradeZeroLocates.Locate(Htbx, 300, q => q.Offers[0]);
            Assert.AreEqual(TradeZeroLocateStatus.Completed, result.Status, result.Message);
            Assert.AreEqual(TradeZeroLocateType.Locate, result.Type);
            Assert.AreEqual(300, result.Quantity);
            Assert.AreEqual(0.03m, result.PricePerShare);
            Assert.AreEqual(300, TradeZeroLocates.GetAvailable(Htbx));
            Assert.AreEqual(300, TradeZeroLocates.GetAvailable(Htbx, TradeZeroLocateType.Locate));
            Assert.AreEqual(0, TradeZeroLocates.GetAvailable(Htbx, TradeZeroLocateType.PreBorrow));

            Assert.AreEqual(TradeZeroLocateStatus.Rejected, TradeZeroLocates.Locate(Htbx, 150, q => q.Offers[0]).Status);
            Assert.AreEqual(TradeZeroLocateStatus.Declined, TradeZeroLocates.Locate(Htbx, 100, _ => null).Status);
        }

        [Test]
        public void SellBackIsQueuedThenSettles()
        {
            using var h = new TradeZeroHarness("short_htb", new { locate_inventory = new { HTBX = 200 } });
            var result = TradeZeroLocates.SellBack(Htbx, 200);
            Assert.AreEqual(TradeZeroLocateStatus.Pending, result.Status, result.Message);
            Assert.AreEqual(TradeZeroLocateType.Locate, result.Type);
            Assert.AreEqual(200, TradeZeroLocates.GetInventory(Htbx).Single().ToBeSold);
            Assert.IsTrue(h.Messages.Any(m => m.Code == "LocateSoldBack"));

            h.Advance(TimeSpan.FromMinutes(1));
            var inventory = TradeZeroLocates.GetInventory(Htbx).Single();
            Assert.AreEqual(200, inventory.Sold);
            Assert.AreEqual(0, inventory.Available);
            Assert.IsTrue(TradeZeroLocates.GetHistory().Single().IsSellBack);
        }

        [Test]
        public void SellBackInputIsCheckedBeforeSending()
        {
            using var h = new TradeZeroHarness("short_htb", new { locate_inventory = new { HTBX = 200, REGSHO = new { PreBorrow = 100, SingleUse = 100 } } });
            StringAssert.Contains("lots of 100", TradeZeroLocates.SellBack(Htbx, 150).Message);
            StringAssert.Contains("only 200", TradeZeroLocates.SellBack(Htbx, 300).Message);
            StringAssert.Contains("several locate types", TradeZeroLocates.SellBack(Regsho, 100).Message);
            Assert.IsEmpty(TradeZeroLocates.GetHistory(), "nothing was sent");

            var result = TradeZeroLocates.SellBack(Regsho, 100, TradeZeroLocateType.PreBorrow);
            Assert.AreEqual(TradeZeroLocateStatus.Pending, result.Status, result.Message);
            var rows = TradeZeroLocates.GetInventory(Regsho).ToDictionary(i => i.Type);
            Assert.AreEqual(100, rows[TradeZeroLocateType.PreBorrow].ToBeSold);
            Assert.AreEqual(100, rows[TradeZeroLocateType.SingleUse].Available);
        }

        [Test]
        public void SellBackUnusedSellsWholeLotsOfEveryTypeAndReportsAUsedSingleUse()
        {
            using var h = new TradeZeroHarness("short_htb", new
            {
                locate_inventory = new { HTBX = 250, REGSHO = new { PreBorrow = 100, SingleUse = 200 } },
                locate_use_order = new[] { 4, 3, 1, 2 }
            });
            var results = TradeZeroLocates.SellBackUnused(Htbx);
            Assert.AreEqual(200, results.Single().Quantity);
            Assert.AreEqual(TradeZeroLocateStatus.Pending, results.Single().Status);
            Assert.AreEqual(50, TradeZeroLocates.GetAvailable(Htbx));

            // a short uses 100 SingleUse shares: the other 100 can no longer be sold back
            var order = Short(Regsho, 100);
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Filled);
            var regsho = TradeZeroLocates.SellBackUnused(Regsho).ToDictionary(r => r.Type);
            Assert.AreEqual(TradeZeroLocateStatus.Pending, regsho[TradeZeroLocateType.PreBorrow].Status);
            Assert.AreEqual(TradeZeroLocateStatus.Rejected, regsho[TradeZeroLocateType.SingleUse].Status);
        }

        [Test]
        public void LocatesAreClearedAtTheNextDay()
        {
            using var h = new TradeZeroHarness("short_htb", new { locate_inventory = new { HTBX = 200 } });
            Assert.AreEqual(200, TradeZeroLocates.GetAvailable(Htbx));
            h.AdvanceTo(new DateTime(2026, 1, 6, 14, 31, 0, DateTimeKind.Utc));
            Assert.AreEqual(0, TradeZeroLocates.GetAvailable(Htbx));
        }

        [Test]
        public void WithoutAConnectedBrokerageReadsAreEmptyAndActionsRejected()
        {
            using var h = new TradeZeroHarness("short_htb", new { locate_inventory = new { HTBX = 200 } });
            h.Brokerage.Disconnect();
            Assert.IsFalse(TradeZeroLocates.IsAvailable);
            Assert.AreEqual(0, TradeZeroLocates.GetAvailable(Htbx));
            Assert.IsEmpty(TradeZeroLocates.GetInventory());
            Assert.IsEmpty(TradeZeroLocates.GetHistory());
            Assert.IsNull(TradeZeroLocates.IsEasyToBorrow(Htbx));
            StringAssert.Contains("not connected", TradeZeroLocates.SellBack(Htbx, 100).Message);
            StringAssert.Contains("not connected", TradeZeroLocates.Locate(Htbx, 100, q => q.Offers[0]).Message);
        }
    }
}
