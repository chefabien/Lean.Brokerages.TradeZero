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
        // Hard-to-borrow shorts that need a new locate: PlaceOrder emits Submitted and returns; a worker runs the locate
        // workflow (quote, offers, handler, accept) without holding _orderLock, under the symbol's semaphore, then submits
        // the first leg. While waiting the order is AwaitingLocate: a cancel stops the workflow, an update is refused.

        private TradeZeroLocateManager _locates;

        /// <summary>Locate workflows of this brokerage (used by <see cref="TradeZeroLocates"/>)</summary>
        internal TradeZeroLocateManager LocateManager => _locates;

        internal TimeSpan LocateTimeout => _settings.LocateTimeout;

        internal string TickerOf(Symbol symbol) => _symbolMapper.GetBrokerageSymbol(symbol);

        internal Symbol LeanSymbolOf(string ticker) => ResolveLeanSymbol(ticker);

        internal bool? EasyToBorrow(string ticker) => ResolveEasyToBorrow(ticker);

        private void InitializeLocates()
        {
            _locates = new TradeZeroLocateManager(_client, () => _accountId, _settings, _apiLogger,
                (type, code, message) =>
                {
                    _apiLogger.LogEvent("lean", code, new { message });
                    OnMessage(new BrokerageMessageEvent(type, code, message));
                },
                _runTag);
        }

        /// <summary>
        /// Registers the order as AwaitingLocate, emits Submitted and starts the locate worker. Called under _orderLock.
        /// </summary>
        private bool StartLocateWorkflow(TrackedOrder tracked, IReadOnlyList<TradeZeroLegPlan> legs, long shortQuantity,
            Func<TradeZeroLocateQuote, TradeZeroLocateOffer> handler)
        {
            tracked.AwaitingLocate = true;
            tracked.LocateCancel = new CancellationTokenSource();
            _ordersByLeanId[tracked.LeanOrderId] = tracked;
            _apiLogger.LogEvent("lean", "awaiting locate", new { leanOrderId = tracked.LeanOrderId, symbol = tracked.Ticker, shortQuantity, handler = handler != null });
            OnOrderEvent(new OrderEvent(tracked.Order, DateTime.UtcNow, OrderFee.Zero, "TradeZero order waiting for a locate") { Status = OrderStatus.Submitted });
            var token = tracked.LocateCancel.Token;
            Task.Run(() => RunLocateWorkflow(tracked, legs, shortQuantity, handler, token));
            return true;
        }

        private void RunLocateWorkflow(TrackedOrder tracked, IReadOnlyList<TradeZeroLegPlan> legs, long shortQuantity,
            Func<TradeZeroLocateQuote, TradeZeroLocateOffer> handler, CancellationToken token)
        {
            var gate = _locates.SymbolLock(tracked.Ticker);
            try
            {
                gate.Wait(token);
            }
            catch (OperationCanceledException)
            {
                // canceled while another order of the symbol was locating: CancelOrder reported it
                return;
            }

            TradeZeroLocateOffer accepted = null;
            try
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }
                string refusal = null;
                // fresh read inside the lock: earlier orders of this symbol have reserved their shares by now
                var available = _locates.Available(tracked.Ticker);
                var need = shortQuantity - available;
                if (need > 0 && handler == null)
                {
                    refusal = NoLocateHandlerMessage(tracked.Ticker, shortQuantity, available);
                }
                else if (need > 0)
                {
                    var request = new LocateRequest(tracked.Ticker, tracked.Order.Symbol, tracked.LeanOrderId, tracked.Order.Quantity,
                        shortQuantity, available, TradeZeroLocateManager.RoundUpToLot(need));
                    var outcome = _locates.Acquire(request, handler, token);
                    accepted = outcome.Accepted;
                    switch (outcome.Kind)
                    {
                        case LocateOutcomeKind.Filled:
                            if (!accepted.CoversOrder)
                            {
                                refusal = $"{tracked.Ticker}: not enough shares located: {available} held + {accepted.Quantity} bought < {shortQuantity} " +
                                    "(the located shares stay in the inventory)";
                            }
                            break;
                        case LocateOutcomeKind.EasyToBorrow:
                            // the symbol became easy to borrow: no locate needed
                            _easyToBorrowCache.TryRemove(tracked.Ticker, out _);
                            break;
                        case LocateOutcomeKind.Canceled:
                            return;
                        case LocateOutcomeKind.Declined:
                            Warn("LocateDeclined", $"{tracked.Ticker}: order {tracked.LeanOrderId} not sent: {outcome.Message}.");
                            refusal = $"{tracked.Ticker} locate declined: {outcome.Message}";
                            break;
                        case LocateOutcomeKind.Expired:
                            refusal = $"{tracked.Ticker}: {outcome.Message}";
                            break;
                        default:
                            refusal = $"{tracked.Ticker} is hard to borrow and no locate was obtained: {outcome.Message}";
                            break;
                    }
                }
                FinishLocateWorkflow(tracked, legs, accepted, refusal);
            }
            catch (Exception e)
            {
                Log.Error(e, $"TradeZeroBrokerage.RunLocateWorkflow(): {tracked.LeanOrderId}");
                FinishLocateWorkflow(tracked, legs, accepted, $"{tracked.Ticker}: locate failed: {e.Message}");
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>
        /// Ends the AwaitingLocate state: submits the first leg, or ends the order Invalid. Still under the symbol's lock,
        /// so the broker reserves the shares before the next order of the symbol reads the inventory.
        /// </summary>
        private void FinishLocateWorkflow(TrackedOrder tracked, IReadOnlyList<TradeZeroLegPlan> legs, TradeZeroLocateOffer accepted, string refusal)
        {
            lock (_orderLock)
            {
                tracked.AwaitingLocate = false;
                if (tracked.Done)
                {
                    ReportUnusedLocate(tracked, accepted, "it was canceled");
                    return;
                }
                if (refusal == null)
                {
                    tracked.LocateCost = accepted?.TotalCost ?? 0m;
                    try
                    {
                        var result = SubmitLeg(tracked, legs[0]);
                        if (result.Placed)
                        {
                            var order = _orderProvider.GetOrderById(tracked.LeanOrderId) ?? tracked.Order;
                            NotifyBrokerIds(tracked, order);
                            if (result.Row != null)
                            {
                                ApplyLegUpdate(tracked, result.Leg, result.Row);
                            }
                            PollOrdersSoon(TimeSpan.FromMilliseconds(250));
                            return;
                        }
                        refusal = result.Message;
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, $"TradeZeroBrokerage.FinishLocateWorkflow(): {tracked.LeanOrderId}");
                        refusal = $"TradeZero order failed: {e.Message}";
                    }
                }
                tracked.Done = true;
                tracked.PendingLeg = null;
                _apiLogger.LogEvent("lean", "invalid", new { leanOrderId = tracked.LeanOrderId, message = refusal });
                EmitEvent(tracked, OrderStatus.Invalid, null, refusal);
                if (accepted != null && !refusal.Contains("stay in the inventory", StringComparison.Ordinal))
                {
                    ReportUnusedLocate(tracked, accepted, "it was not sent");
                }
            }
        }

        private void ReportUnusedLocate(TrackedOrder tracked, TradeZeroLocateOffer accepted, string why)
        {
            if (accepted == null)
            {
                return;
            }
            Warn("LocateUnused", $"{tracked.Ticker}: the locate bought for order {tracked.LeanOrderId} ({accepted.Quantity} {accepted.Type} shares, " +
                $"{accepted.TotalCost} USD) is unused because {why}. The shares stay in the inventory: TradeZeroLocates.SellBack sells them back.");
        }

        /// <summary>
        /// UpdateOrder runs synchronously and cannot buy a locate: refuse, before anything is canceled, an update whose
        /// replacement would short more than the located shares (the current leg's own reservation counts as available)
        /// </summary>
        private string CheckUpdateLocate(TrackedOrder tracked, Order order)
        {
            var remaining = tracked.Sign * (order.AbsoluteQuantity - tracked.FilledQuantity);
            if (remaining == 0)
            {
                return null;
            }
            var legs = TradeZeroOrderMapper.PlanLegs(_securityProvider.GetHoldingsQuantity(order.Symbol), remaining);
            var shortLeg = legs.FirstOrDefault(IsShortLeg);
            if (shortLeg == null || ResolveEasyToBorrow(tracked.Ticker) != false)
            {
                return null;
            }
            var available = TryReadAvailable(tracked.Ticker);
            if (available == null)
            {
                return null;
            }
            var current = tracked.Current;
            var reserved = current != null && !current.Terminal && IsShortLeg(current.Plan) ? (long)Math.Max(0m, current.Plan.Quantity - current.Executed) : 0L;
            var missing = shortLeg.Quantity - available.Value - reserved;
            return missing > 0
                ? $"{tracked.Ticker} is hard to borrow and the new quantity needs {missing} more located shares; cancel and re-place the order with a " +
                  "LocateHandler, or call TradeZeroLocates.Locate first"
                : null;
        }
    }
}
