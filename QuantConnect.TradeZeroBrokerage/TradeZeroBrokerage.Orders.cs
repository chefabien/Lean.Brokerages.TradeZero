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
using System.Threading.Tasks;
using QuantConnect.Brokerages.TradeZero.Api;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.TradeZero
{
    public partial class TradeZeroBrokerage
    {
        // How a Lean order maps to TradeZero orders ("legs"):
        // - usually one leg;
        // - an order crossing zero: a closing leg, then an opening leg placed when the first one is filled;
        // - an updated order: the current leg is canceled and a new leg is placed for the remaining quantity.
        // Fills are accumulated over all legs, so Lean sees one order. All state below is guarded by _orderLock;
        // Lean order events are queued while holding it, so they are delivered in order (TradeZeroBrokerage.Events.cs),
        // and orders are planned from HoldingsQuantity: Lean's holdings plus the fills not delivered yet.

        private sealed class Leg
        {
            public string ClientOrderId;
            public TradeZeroLegPlan Plan;
            public decimal Executed;
            public decimal AveragePrice;
            public string Status = TzOrderStatus.PendingNew;
            public bool Terminal;
            public bool CancelForReplace;
            public DateTime PlacedUtc;
            public int MissingPolls;
        }

        private sealed class TrackedOrder
        {
            public int LeanOrderId;
            public string Ticker;
            public Order Order;
            public int Sign;
            public decimal TotalQuantity;
            public decimal FilledQuantity;
            public readonly List<Leg> Legs = new();
            public TradeZeroLegPlan PendingLeg;
            public bool CrossesZero;
            public bool Done;
            public bool FirstFillCharged;
            public int NextSequence = 1;
            /// <summary>Waiting for the locate workflow: nothing has been sent to the broker yet</summary>
            public bool AwaitingLocate;
            public CancellationTokenSource LocateCancel;
            /// <summary>Cost of the locate bought for this order, added to the first fill's fee</summary>
            public decimal LocateCost;
            public Leg Current => Legs.Count == 0 ? null : Legs[^1];
            public List<string> BrokerIds => Legs.Select(l => l.ClientOrderId).ToList();
        }

        private record SubmitResult(bool Placed, Leg Leg, TzOrder Row, string Message);

        private readonly object _orderLock = new();
        private readonly Dictionary<int, TrackedOrder> _ordersByLeanId = new();
        private readonly Dictionary<string, (TrackedOrder Order, Leg Leg)> _legsByClientOrderId = new();
        private readonly HashSet<string> _reportedUnknownOrders = new();
        private readonly HashSet<string> _listedOpenOrderIds = new();
        private volatile bool _openOrdersListed;
        private readonly string _runTag;

        #region Lean order API

        public override List<Order> GetOpenOrders()
        {
            var result = new List<Order>();
            foreach (var row in _client.GetOrders(_accountId))
            {
                if (TzOrderStatus.IsTerminal(row.OrderStatus) || !string.Equals(row.SecurityType, TzValues.Stock, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var order = TradeZeroOrderMapper.ToLeanOrder(row, ResolveLeanSymbol(row.Symbol));
                if (order == null)
                {
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "UnsupportedOrder",
                        $"Open TradeZero order {row.ClientOrderId} ({row.OrderType}) cannot be represented in Lean and is ignored."));
                    continue;
                }
                result.Add(order);
            }
            lock (_orderLock)
            {
                foreach (var order in result)
                {
                    _listedOpenOrderIds.Add(order.BrokerId.Single());
                }
                _openOrdersListed = true;
            }
            _apiLogger.LogEvent("lean", "open orders", result.Select(o => new { brokerId = o.BrokerId.FirstOrDefault(), symbol = o.Symbol.Value, o.Quantity, type = o.Type.ToString() }));
            return result;
        }

        public override bool PlaceOrder(Order order)
        {
            var error = TradeZeroOrderMapper.Validate(order);
            string ticker = null;
            if (error == null)
            {
                try
                {
                    ticker = _symbolMapper.GetBrokerageSymbol(order.Symbol);
                }
                catch (ArgumentException e)
                {
                    error = e.Message;
                }
            }
            if (error == null && !_connected)
            {
                error = "TradeZero brokerage is not connected.";
            }
            if (error != null)
            {
                EmitInvalid(order, error);
                return false;
            }

            lock (_orderLock)
            {
                SemaphoreSlim locateGate = null;
                try
                {
                    var holdings = HoldingsQuantity(order.Symbol);
                    var legs = TradeZeroOrderMapper.PlanLegs(holdings, order.Quantity);
                    var tracked = new TrackedOrder
                    {
                        LeanOrderId = order.Id,
                        Ticker = ticker,
                        Order = order,
                        Sign = order.Quantity > 0 ? 1 : -1,
                        TotalQuantity = order.AbsoluteQuantity,
                        CrossesZero = legs.Count > 1,
                        PendingLeg = legs.Count > 1 ? legs[1] : null
                    };
                    _apiLogger.LogEvent("lean", "place", new { leanOrderId = order.Id, symbol = ticker, quantity = order.Quantity, type = order.Type.ToString(), holdings, legs });

                    var shortLeg = legs.FirstOrDefault(IsShortLeg);
                    if (shortLeg != null && ResolveEasyToBorrow(ticker) == false)
                    {
                        var properties = order.Properties as TradeZeroOrderProperties;
                        var handler = properties?.LocateHandler;
                        if (shortLeg.Quantity % 100 != 0 && properties?.SuppressOddLotLocateWarning != true)
                        {
                            Warn("LocateOddLot", $"{ticker}: short of {shortLeg.Quantity} shares is not a multiple of 100. Locates are sold, and can only " +
                                $"be sold back, in lots of 100, so {RoundUpToLotRemainder(shortLeg.Quantity)} located shares will stay unused. " +
                                "Set SuppressOddLotLocateWarning to hide this.");
                        }

                        // the inventory can be checked right away unless another locate action holds the symbol
                        locateGate = _locates.SymbolLock(ticker);
                        if (!locateGate.Wait(0))
                        {
                            locateGate = null;
                            return StartLocateWorkflow(tracked, legs, shortLeg.Quantity, handler);
                        }
                        var available = TryReadAvailable(ticker);
                        if (available == null || available >= shortLeg.Quantity)
                        {
                            // covered by the inventory (or unknown: TradeZero decides): submit as usual, holding the lock
                            // until the broker has reserved the shares
                        }
                        else if (handler == null)
                        {
                            EmitInvalid(order, NoLocateHandlerMessage(ticker, shortLeg.Quantity, available.Value));
                            return false;
                        }
                        else
                        {
                            locateGate.Release();
                            locateGate = null;
                            return StartLocateWorkflow(tracked, legs, shortLeg.Quantity, handler);
                        }
                    }

                    var result = SubmitLeg(tracked, legs[0]);
                    if (!result.Placed)
                    {
                        EmitInvalid(order, result.Message);
                        return false;
                    }

                    _ordersByLeanId[order.Id] = tracked;
                    if (!order.BrokerId.Contains(result.Leg.ClientOrderId))
                    {
                        order.BrokerId.Add(result.Leg.ClientOrderId);
                    }
                    OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "TradeZero order submitted") { Status = OrderStatus.Submitted });
                    if (result.Row != null)
                    {
                        ApplyLegUpdate(tracked, result.Leg, result.Row);
                    }
                    PollOrdersSoon(TimeSpan.FromMilliseconds(250));
                    return true;
                }
                catch (Exception e)
                {
                    Log.Error(e, $"TradeZeroBrokerage.PlaceOrder(): {order.Id}");
                    EmitInvalid(order, $"TradeZero order failed: {e.Message}");
                    return false;
                }
                finally
                {
                    locateGate?.Release();
                }
            }
        }

        /// <summary>
        /// Cancel-then-replace: TradeZero has no modify endpoint.
        /// 1. cancel the current TradeZero order and wait until it is final;
        /// 2. apply the fills it got meanwhile;
        /// 3. place a new TradeZero order for (new quantity - filled) with the new prices.
        /// If step 3 fails the Lean order ends Canceled. Nothing works at the broker between 1 and 3.
        /// </summary>
        public override bool UpdateOrder(Order order)
        {
            lock (_orderLock)
            {
                try
                {
                    var tracked = FindTracked(order);
                    string refusal = null;
                    if (tracked == null || tracked.Done)
                    {
                        refusal = "the order is not working at TradeZero anymore";
                    }
                    else if (tracked.AwaitingLocate)
                    {
                        refusal = "waiting for a locate";
                    }
                    else if (order.AbsoluteQuantity <= tracked.FilledQuantity)
                    {
                        refusal = $"new quantity {order.AbsoluteQuantity} is not above the filled quantity {tracked.FilledQuantity}";
                    }
                    else if (tracked.Current.Terminal)
                    {
                        refusal = "the TradeZero order is already final (a zero-crossing order between its two parts)";
                    }
                    else
                    {
                        refusal = CheckUpdateLocate(tracked, order);
                    }
                    if (refusal != null)
                    {
                        Warn("UpdateRejected", $"Order {order.Id} not updated: {refusal}.");
                        return false;
                    }

                    var leg = tracked.Current;
                    _apiLogger.LogEvent("lean", "update: cancel current leg", new { leanOrderId = order.Id, clientOrderId = leg.ClientOrderId, newQuantity = order.Quantity });
                    leg.CancelForReplace = true;
                    var row = CancelLegAndWait(leg);
                    if (row == null)
                    {
                        leg.CancelForReplace = false;
                        Warn("UpdateRejected", $"Order {order.Id} not updated: TradeZero did not accept the cancel of {leg.ClientOrderId}.");
                        return false;
                    }
                    ApplyLegUpdate(tracked, leg, row);
                    if (!leg.Terminal)
                    {
                        // cancel not confirmed in time: keep tracking the leg as a normal order
                        leg.CancelForReplace = false;
                        Warn("UpdateRejected", $"Order {order.Id} not updated: the cancel of {leg.ClientOrderId} was not confirmed within {_settings.ReplaceCancelTimeout.TotalSeconds}s ({row.OrderStatus}).");
                        return false;
                    }
                    if (row.OrderStatus == TzOrderStatus.Filled || tracked.Done)
                    {
                        leg.CancelForReplace = false;
                        Warn("UpdateRejected", $"Order {order.Id} not updated: it was filled before the cancel.");
                        return false;
                    }

                    tracked.Order = order;
                    tracked.TotalQuantity = order.AbsoluteQuantity;
                    // re-plan the rest from the current position: it may now close, open, or cross zero
                    var remaining = tracked.Sign * (tracked.TotalQuantity - tracked.FilledQuantity);
                    var holdings = HoldingsQuantity(order.Symbol);
                    var legs = TradeZeroOrderMapper.PlanLegs(holdings, remaining);
                    tracked.CrossesZero = legs.Count > 1;
                    tracked.PendingLeg = legs.Count > 1 ? legs[1] : null;
                    _apiLogger.LogEvent("lean", "update: replace", new { leanOrderId = order.Id, holdings, remaining, legs });

                    SubmitResult result;
                    long? located = null;
                    if (IsShortLeg(legs[0]) && ResolveEasyToBorrow(tracked.Ticker) == false &&
                        (located = TryReadAvailable(tracked.Ticker)) < legs[0].Quantity)
                    {
                        result = new SubmitResult(false, null, null,
                            $"{tracked.Ticker} is hard to borrow and only {located} located shares are available for {legs[0].Quantity} short shares");
                    }
                    else
                    {
                        result = SubmitLeg(tracked, legs[0]);
                    }
                    if (!result.Placed)
                    {
                        tracked.Done = true;
                        EmitEvent(tracked, OrderStatus.Canceled, null,
                            $"Update failed: the previous TradeZero order was canceled and the replacement was rejected: {result.Message}");
                        return false;
                    }

                    NotifyBrokerIds(tracked, order);
                    EmitEvent(tracked, OrderStatus.UpdateSubmitted, null, $"TradeZero order replaced by {result.Leg.ClientOrderId}");
                    if (result.Row != null)
                    {
                        ApplyLegUpdate(tracked, result.Leg, result.Row);
                    }
                    PollOrdersSoon(TimeSpan.FromMilliseconds(250));
                    return true;
                }
                catch (Exception e)
                {
                    Log.Error(e, $"TradeZeroBrokerage.UpdateOrder(): {order.Id}");
                    Warn("UpdateFailed", $"Order {order.Id} update failed: {e.Message}");
                    return false;
                }
            }
        }

        public override bool CancelOrder(Order order)
        {
            lock (_orderLock)
            {
                try
                {
                    var tracked = FindTracked(order);
                    if (tracked == null || tracked.Done)
                    {
                        Warn("CancelRejected", $"Order {order.Id} not canceled: it is not working at TradeZero.");
                        return false;
                    }
                    tracked.PendingLeg = null;
                    if (tracked.AwaitingLocate)
                    {
                        // nothing sent yet: the locate workflow stops (open offers are canceled; a locate already bought stays)
                        tracked.Done = true;
                        tracked.LocateCancel?.Cancel();
                        _apiLogger.LogEvent("lean", "cancel while awaiting locate", new { leanOrderId = order.Id });
                        EmitEvent(tracked, OrderStatus.Canceled, null, "Canceled while waiting for a locate");
                        return true;
                    }
                    var leg = tracked.Current;
                    if (leg.Terminal)
                    {
                        // between the two legs of a zero-crossing order: nothing working at the broker
                        tracked.Done = true;
                        EmitEvent(tracked, OrderStatus.Canceled, null, "Canceled before the opening part was placed");
                        return true;
                    }
                    _apiLogger.LogEvent("lean", "cancel", new { leanOrderId = order.Id, clientOrderId = leg.ClientOrderId });
                    var row = _client.CancelOrder(_accountId, leg.ClientOrderId);
                    if (row == null)
                    {
                        Warn("CancelRejected", $"Order {order.Id} not canceled: TradeZero does not know {leg.ClientOrderId}.");
                        return false;
                    }
                    var refused = row.Text != null && row.Text.StartsWith("R130", StringComparison.Ordinal);
                    ApplyLegUpdate(tracked, leg, row);
                    if (refused && !TzOrderStatus.IsTerminal(row.OrderStatus))
                    {
                        Warn("CancelRejected", $"Order {order.Id} not canceled: {row.Text}");
                        return false;
                    }
                    if (!leg.Terminal)
                    {
                        // PendingCancel: the poll reports Canceled
                        PollOrdersSoon(TimeSpan.FromMilliseconds(250));
                    }
                    return !refused || tracked.Done;
                }
                catch (Exception e)
                {
                    Log.Error(e, $"TradeZeroBrokerage.CancelOrder(): {order.Id}");
                    Warn("CancelFailed", $"Order {order.Id} cancel failed: {e.Message}");
                    return false;
                }
            }
        }

        #endregion

        #region Leg handling

        private SubmitResult SubmitLeg(TrackedOrder tracked, TradeZeroLegPlan plan)
        {
            var clientOrderId = NewClientOrderId(tracked);
            var request = TradeZeroOrderMapper.BuildRequest(tracked.Order, tracked.Ticker, plan, clientOrderId, _route);
            _apiLogger.LogEvent("lean", "submit leg", new { leanOrderId = tracked.LeanOrderId, request });

            TzOrder row;
            try
            {
                row = _client.PlaceOrder(_accountId, request);
            }
            catch (TradeZeroApiException e) when (e.IsOutcomeUnknown)
            {
                // never resend blindly: look the order up by its client order id
                _apiLogger.LogEvent("lean", "submit outcome unknown, checking", new { clientOrderId, error = e.Message });
                row = LookUpAfterUncertainSubmit(clientOrderId);
                if (row == null)
                {
                    return new SubmitResult(false, null, null, $"TradeZero did not confirm the order ({e.Message}) and does not know {clientOrderId}.");
                }
            }
            catch (TradeZeroApiException e)
            {
                return new SubmitResult(false, null, null, $"TradeZero rejected the order: {e.ServerMessage}");
            }

            var leg = new Leg { ClientOrderId = clientOrderId, Plan = plan, PlacedUtc = DateTime.UtcNow };
            tracked.Legs.Add(leg);
            _legsByClientOrderId[clientOrderId] = (tracked, leg);

            if (row.OrderStatus == TzOrderStatus.Rejected)
            {
                leg.Status = row.OrderStatus;
                leg.Terminal = true;
                return new SubmitResult(false, leg, row, $"TradeZero rejected the order: {row.Text}");
            }
            return new SubmitResult(true, leg, row, null);
        }

        private TzOrder LookUpAfterUncertainSubmit(string clientOrderId)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(500 * (attempt + 1)));
                try
                {
                    var row = _client.GetOrder(_accountId, clientOrderId);
                    if (row != null)
                    {
                        return row;
                    }
                }
                catch (TradeZeroApiException e)
                {
                    Log.Error($"TradeZeroBrokerage: looking up {clientOrderId} failed: {e.Message}");
                }
            }
            return null;
        }

        private TzOrder CancelLegAndWait(Leg leg)
        {
            var row = _client.CancelOrder(_accountId, leg.ClientOrderId);
            if (row == null)
            {
                return null;
            }
            var deadline = DateTime.UtcNow + _settings.ReplaceCancelTimeout;
            while (!TzOrderStatus.IsTerminal(row.OrderStatus) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(250);
                row = _client.GetOrder(_accountId, leg.ClientOrderId) ?? row;
            }
            return row;
        }

        /// <summary>
        /// Applies a TradeZero order row to its leg: emits fills for the newly executed shares, then handles
        /// a final status. Idempotent and monotonic: stale rows (fewer shares executed, or a leg already final)
        /// change nothing.
        /// </summary>
        private void ApplyLegUpdate(TrackedOrder tracked, Leg leg, TzOrder row)
        {
            if (leg.Terminal)
            {
                return;
            }
            leg.MissingPolls = 0;

            if (row.Executed > leg.Executed && !tracked.Done)
            {
                var quantity = row.Executed - leg.Executed;
                var price = TradeZeroOrderMapper.IncrementalFillPrice(leg.Executed, leg.AveragePrice, row.Executed, row.PriceAvg, row.LastPrice);
                leg.Executed = row.Executed;
                leg.AveragePrice = row.PriceAvg;
                EmitFill(tracked, leg, quantity, price);
            }

            var previous = leg.Status;
            leg.Status = row.OrderStatus;
            if (previous != row.OrderStatus)
            {
                _apiLogger.LogEvent("lean", "leg status", new { leanOrderId = tracked.LeanOrderId, clientOrderId = leg.ClientOrderId, from = previous, to = row.OrderStatus, executed = row.Executed, text = row.Text });
            }
            if (!TzOrderStatus.IsTerminal(row.OrderStatus))
            {
                return;
            }
            leg.Terminal = true;

            switch (row.OrderStatus)
            {
                case TzOrderStatus.Filled:
                    if (tracked.PendingLeg != null && !tracked.Done)
                    {
                        PlacePendingLeg(tracked);
                    }
                    break;

                case TzOrderStatus.Rejected:
                    if (!leg.CancelForReplace && !tracked.Done)
                    {
                        tracked.Done = true;
                        tracked.PendingLeg = null;
                        EmitEvent(tracked, tracked.FilledQuantity == 0 ? OrderStatus.Invalid : OrderStatus.Canceled, null, $"TradeZero rejected the order: {row.Text}");
                    }
                    break;

                default:
                    // Canceled, Expired, DoneForDay
                    if (!leg.CancelForReplace && !tracked.Done)
                    {
                        tracked.Done = true;
                        tracked.PendingLeg = null;
                        var text = string.IsNullOrEmpty(row.Text) || row.Text == "TRAFIX_SIM" ? string.Empty : $": {row.Text}";
                        EmitEvent(tracked, OrderStatus.Canceled, null, $"TradeZero order {row.OrderStatus}{text}");
                    }
                    break;
            }
        }

        /// <summary>
        /// Second part of a zero-crossing order, once the closing part is filled
        /// </summary>
        private void PlacePendingLeg(TrackedOrder tracked)
        {
            var plan = tracked.PendingLeg;
            tracked.PendingLeg = null;
            _apiLogger.LogEvent("lean", "cross zero: place opening leg", new { leanOrderId = tracked.LeanOrderId, plan });

            long? located = null;
            if (IsShortLeg(plan) && ResolveEasyToBorrow(tracked.Ticker) == false && (located = TryReadAvailable(tracked.Ticker)) < plan.Quantity)
            {
                tracked.Done = true;
                EmitEvent(tracked, OrderStatus.Canceled, null, $"Position closed, but {tracked.Ticker} is hard to borrow and only {located} located shares " +
                    $"are available for the {plan.Quantity} short shares: the short part was not placed.");
                return;
            }
            var result = SubmitLeg(tracked, plan);
            if (!result.Placed)
            {
                tracked.Done = true;
                EmitEvent(tracked, OrderStatus.Canceled, null, $"Position closed, but the opening part was rejected: {result.Message}");
                return;
            }
            var leanOrder = _orderProvider.GetOrderById(tracked.LeanOrderId) ?? tracked.Order;
            NotifyBrokerIds(tracked, leanOrder);
            if (result.Row != null)
            {
                ApplyLegUpdate(tracked, result.Leg, result.Row);
            }
            PollOrdersSoon(TimeSpan.FromMilliseconds(250));
        }

        private void EmitFill(TrackedOrder tracked, Leg leg, decimal quantity, decimal price)
        {
            tracked.FilledQuantity += quantity;
            var status = tracked.FilledQuantity >= tracked.TotalQuantity ? OrderStatus.Filled : OrderStatus.PartiallyFilled;
            if (status == OrderStatus.Filled)
            {
                tracked.Done = true;
            }
            var fee = _feeModel.GetFillFee(quantity, !tracked.FirstFillCharged);
            if (!tracked.FirstFillCharged && tracked.LocateCost > 0)
            {
                // the locate bought for this order is a cost of its first fill (TradeZero charged it at the accept)
                fee = new OrderFee(new Securities.CashAmount(fee.Value.Amount + tracked.LocateCost, Currencies.USD));
            }
            tracked.FirstFillCharged = true;
            _apiLogger.LogEvent("lean", "fill", new { leanOrderId = tracked.LeanOrderId, clientOrderId = leg.ClientOrderId, quantity = tracked.Sign * quantity, price, filled = tracked.FilledQuantity, total = tracked.TotalQuantity, status = status.ToString() });
            EmitEvent(tracked, status, fee, $"TradeZero fill ({leg.ClientOrderId})", tracked.Sign * quantity, price);
        }

        private void EmitEvent(TrackedOrder tracked, OrderStatus status, OrderFee fee, string message, decimal fillQuantity = 0, decimal fillPrice = 0)
        {
            var order = _orderProvider.GetOrderById(tracked.LeanOrderId) ?? tracked.Order;
            var orderEvent = new OrderEvent(order, DateTime.UtcNow, fee ?? OrderFee.Zero, message)
            {
                Status = status,
                FillQuantity = fillQuantity,
                FillPrice = fillPrice
            };
            OnOrderEvent(orderEvent);
        }

        private void EmitInvalid(Order order, string message)
        {
            _apiLogger.LogEvent("lean", "invalid", new { leanOrderId = order.Id, message });
            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, message) { Status = OrderStatus.Invalid });
        }

        private void NotifyBrokerIds(TrackedOrder tracked, Order order)
        {
            var ids = tracked.BrokerIds;
            if (order != null)
            {
                foreach (var id in ids.Where(id => !order.BrokerId.Contains(id)))
                {
                    order.BrokerId.Add(id);
                }
            }
            OnOrderIdChangedEvent(new BrokerageOrderIdChangedEvent { OrderId = tracked.LeanOrderId, BrokerId = ids });
        }

        private void Warn(string code, string message)
        {
            _apiLogger.LogEvent("lean", code, new { message });
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, code, message));
        }

        private string NewClientOrderId(TrackedOrder tracked)
        {
            // unique across restarts (run tag) because TradeZero rejects any reused id (R114), even after a cancel
            return $"LN{_runTag}.{tracked.LeanOrderId}.{tracked.NextSequence++}";
        }

        private static bool IsShortLeg(TradeZeroLegPlan plan) => plan.Side == TzValues.Sell && plan.OpenClose == TzValues.Open;

        private static long RoundUpToLotRemainder(long shares) => TradeZeroLocateManager.RoundUpToLot(shares) - shares;

        private static string NoLocateHandlerMessage(string ticker, long shortQuantity, long available)
        {
            return $"{ticker} is hard to borrow: shorting {shortQuantity} needs {shortQuantity - available} more located shares ({available} held). " +
                "Set TradeZeroOrderProperties.LocateHandler to buy a locate.";
        }

        /// <summary>Located shares of the symbol available now (all types), null when the inventory cannot be read</summary>
        private long? TryReadAvailable(string ticker)
        {
            try
            {
                return _locates.Available(ticker);
            }
            catch (Exception e)
            {
                Log.Error($"TradeZeroBrokerage: reading the locate inventory for {ticker} failed: {e.Message}");
                return null;
            }
        }

        private bool HasWorkingOrders()
        {
            lock (_orderLock)
            {
                return _ordersByLeanId.Values.Any(t => !t.Done);
            }
        }

        #endregion

        #region Polling of orders

        private void PollOrders()
        {
            var rows = _client.GetOrders(_accountId);
            List<Leg> missing;
            lock (_orderLock)
            {
                var seen = new HashSet<string>();
                foreach (var row in rows)
                {
                    if (string.IsNullOrEmpty(row.ClientOrderId))
                    {
                        continue;
                    }
                    seen.Add(row.ClientOrderId);
                    if (_legsByClientOrderId.TryGetValue(row.ClientOrderId, out var entry))
                    {
                        ApplyLegUpdate(entry.Order, entry.Leg, row);
                    }
                    else if (!TzOrderStatus.IsTerminal(row.OrderStatus))
                    {
                        TryAdopt(row);
                    }
                }

                // a working leg missing from the list (e.g. dropped from "today's orders"): ask for it directly
                missing = _ordersByLeanId.Values
                    .Where(t => !t.Done)
                    .SelectMany(t => t.Legs)
                    .Where(l => !l.Terminal && !seen.Contains(l.ClientOrderId) && DateTime.UtcNow - l.PlacedUtc > TimeSpan.FromSeconds(2))
                    .Where(l => ++l.MissingPolls >= 2)
                    .ToList();
            }

            foreach (var leg in missing)
            {
                var row = _client.GetOrder(_accountId, leg.ClientOrderId);
                if (row == null)
                {
                    continue;
                }
                lock (_orderLock)
                {
                    if (_legsByClientOrderId.TryGetValue(leg.ClientOrderId, out var entry))
                    {
                        ApplyLegUpdate(entry.Order, entry.Leg, row);
                    }
                }
            }
        }

        /// <summary>
        /// Starts tracking a working TradeZero order Lean already knows (from GetOpenOrders after a restart)
        /// </summary>
        private TrackedOrder TryAdopt(TzOrder row)
        {
            var leanOrder = _orderProvider.GetOrdersByBrokerageId(row.ClientOrderId)?
                .FirstOrDefault(o => o.Status.IsOpen());
            if (leanOrder == null)
            {
                // orders returned by GetOpenOrders are being registered by Lean: only warn about the others,
                // and only once Lean has listed the open orders (until then every order is "unknown")
                if (_openOrdersListed && !_listedOpenOrderIds.Contains(row.ClientOrderId) && _reportedUnknownOrders.Add(row.ClientOrderId))
                {
                    Warn("UnknownOrder", $"TradeZero order {row.ClientOrderId} ({row.Side} {row.OrderQuantity} {row.Symbol}) was not placed by this algorithm; it is ignored.");
                }
                return null;
            }
            if (_ordersByLeanId.TryGetValue(leanOrder.Id, out var existing))
            {
                return existing;
            }

            // Lean's quantity is what was still working when GetOpenOrders ran; earlier fills are in the holdings
            var baseline = Math.Max(0m, row.OrderQuantity - leanOrder.AbsoluteQuantity);
            var tracked = new TrackedOrder
            {
                LeanOrderId = leanOrder.Id,
                Ticker = row.Symbol,
                Order = leanOrder,
                Sign = leanOrder.Quantity > 0 ? 1 : -1,
                TotalQuantity = leanOrder.AbsoluteQuantity,
                // a later replacement leg uses the same side/openClose as this order
                NextSequence = 1,
                FirstFillCharged = true
            };
            var openClose = row.OpenClose == TzValues.Close ? TzValues.Close : TzValues.Open;
            var leg = new Leg
            {
                ClientOrderId = row.ClientOrderId,
                Plan = new TradeZeroLegPlan((long)row.OrderQuantity, row.IsBuy ? TzValues.Buy : TzValues.Sell, openClose),
                Executed = baseline,
                AveragePrice = baseline == row.Executed ? row.PriceAvg : 0m,
                Status = row.OrderStatus,
                PlacedUtc = DateTime.UtcNow
            };
            tracked.Legs.Add(leg);
            _ordersByLeanId[leanOrder.Id] = tracked;
            _legsByClientOrderId[row.ClientOrderId] = (tracked, leg);
            _apiLogger.LogEvent("lean", "adopted open order", new { leanOrderId = leanOrder.Id, clientOrderId = row.ClientOrderId, baseline });
            ApplyLegUpdate(tracked, leg, row);
            return tracked;
        }

        private TrackedOrder FindTracked(Order order)
        {
            if (_ordersByLeanId.TryGetValue(order.Id, out var tracked))
            {
                return tracked;
            }
            // restarted algorithm: the Lean order came from GetOpenOrders
            foreach (var brokerId in order.BrokerId.AsEnumerable().Reverse())
            {
                if (_legsByClientOrderId.TryGetValue(brokerId, out var entry))
                {
                    return entry.Order;
                }
                var row = _client.GetOrder(_accountId, brokerId);
                if (row != null && !TzOrderStatus.IsTerminal(row.OrderStatus))
                {
                    return TryAdopt(row);
                }
            }
            return null;
        }

        #endregion
    }
}
