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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.TradeZero
{
    public partial class TradeZeroBrokerage
    {
        // Every callback into Lean (order events, broker id changes, messages) is queued and delivered by one thread,
        // in the order it was raised, never on the thread that raised it.
        //
        // Lean runs the algorithm's OnOrderEvent inside that call, holding its order event lock. Raised in place, a
        // handler that placed orders stalled everything: the poller raised a fill while holding _orderLock, so Lean's
        // transaction worker blocked in PlaceOrder on that lock and Lean gave up waiting for each new order after 1 s
        // (16 s for a 16-stop ladder), with no order status polled meanwhile. And PlaceOrder's own Submitted event
        // blocked the worker on Lean's lock while any handler ran. Queued, neither thread waits for the algorithm,
        // as with Lean's other brokerages, which report order events after PlaceOrder returns.
        //
        // Lean applies a fill to its holdings when the fill is delivered, but orders are planned (open or close,
        // crossing zero) from those holdings: fills raised but not delivered yet are added (HoldingsQuantity).

        private readonly BlockingCollection<Action> _callbacks = new();
        private readonly object _deliveryLock = new();
        private readonly Dictionary<Symbol, decimal> _undeliveredFills = new();
        private Thread _callbackThread;
        private long _callbacksRaised;
        private long _callbacksDelivered;
        private long _fillsVersion;

        private void InitializeCallbacks()
        {
            _callbackThread = new Thread(DeliverCallbacks) { IsBackground = true, Name = "TradeZeroCallbacks" };
            _callbackThread.Start();
        }

        /// <summary>
        /// Queues the order events. Their fills count in <see cref="HoldingsQuantity"/> until delivered.
        /// </summary>
        /// <remarks>Also where <see cref="Brokerage.OnOrderEvent"/> ends up</remarks>
        protected override void OnOrderEvents(List<OrderEvent> orderEvents)
        {
            var fills = orderEvents.Where(e => e.FillQuantity != 0).ToList();
            ChangeUndeliveredFills(fills, 1);
            Enqueue(() =>
            {
                // just before Lean applies them to its holdings
                ChangeUndeliveredFills(fills, -1);
                base.OnOrderEvents(orderEvents);
            });
        }

        protected override void OnOrderIdChangedEvent(BrokerageOrderIdChangedEvent e)
        {
            Enqueue(() => base.OnOrderIdChangedEvent(e));
        }

        protected override void OnMessage(BrokerageMessageEvent e)
        {
            Enqueue(() => base.OnMessage(e));
        }

        /// <summary>
        /// Lean's holdings of the symbol, plus the fills raised and not delivered to Lean yet
        /// </summary>
        /// <remarks>
        /// Read consistently with the deliveries: a fill delivered between the two reads would otherwise count twice
        /// or not at all. Lean applies a delivered fill a few microseconds after it leaves the pending ones, a window
        /// in which it is missed.
        /// </remarks>
        internal decimal HoldingsQuantity(Symbol symbol)
        {
            while (true)
            {
                long version;
                decimal pending;
                lock (_deliveryLock)
                {
                    version = _fillsVersion;
                    pending = _undeliveredFills.GetValueOrDefault(symbol);
                }
                var held = _securityProvider.GetHoldingsQuantity(symbol);
                lock (_deliveryLock)
                {
                    if (version == _fillsVersion)
                    {
                        return held + pending;
                    }
                }
            }
        }

        /// <summary>
        /// Tickers with fills not delivered to Lean yet: Lean's holdings are behind there
        /// </summary>
        private HashSet<string> TickersWithUndeliveredFills()
        {
            lock (_deliveryLock)
            {
                return _undeliveredFills.Keys.Select(s => s.Value.ToUpperInvariant()).ToHashSet();
            }
        }

        /// <summary>
        /// Waits until every callback raised so far has been delivered to Lean. For tests: in an algorithm nothing
        /// needs to wait, and the callback thread itself (an algorithm handler) never does.
        /// </summary>
        /// <returns>False on timeout, or when called from the callback thread</returns>
        internal bool WaitForCallbacks(TimeSpan timeout)
        {
            if (Thread.CurrentThread == _callbackThread)
            {
                return false;
            }
            var target = Interlocked.Read(ref _callbacksRaised);
            var deadline = DateTime.UtcNow + timeout;
            lock (_deliveryLock)
            {
                while (_callbacksDelivered < target)
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero)
                    {
                        return false;
                    }
                    Monitor.Wait(_deliveryLock, left);
                }
            }
            return true;
        }

        private void ChangeUndeliveredFills(List<OrderEvent> fills, int sign)
        {
            if (fills.Count == 0)
            {
                return;
            }
            lock (_deliveryLock)
            {
                foreach (var fill in fills)
                {
                    var quantity = _undeliveredFills.GetValueOrDefault(fill.Symbol) + sign * fill.FillQuantity;
                    if (quantity == 0)
                    {
                        _undeliveredFills.Remove(fill.Symbol);
                    }
                    else
                    {
                        _undeliveredFills[fill.Symbol] = quantity;
                    }
                }
                _fillsVersion++;
            }
        }

        private void Enqueue(Action callback)
        {
            Interlocked.Increment(ref _callbacksRaised);
            try
            {
                _callbacks.Add(callback);
            }
            catch (InvalidOperationException)
            {
                // disposed: nobody listens anymore
                MarkDelivered();
            }
        }

        private void DeliverCallbacks()
        {
            foreach (var callback in _callbacks.GetConsumingEnumerable())
            {
                try
                {
                    callback();
                }
                catch (Exception e)
                {
                    // Lean's invocators catch their handlers' errors: this is a bug guard so delivery never stops
                    Log.Error(e, "TradeZeroBrokerage: delivering a callback to Lean failed");
                }
                finally
                {
                    MarkDelivered();
                }
            }
        }

        private void MarkDelivered()
        {
            lock (_deliveryLock)
            {
                _callbacksDelivered++;
                Monitor.PulseAll(_deliveryLock);
            }
        }

        /// <summary>
        /// Delivers what is queued, then stops the callback thread
        /// </summary>
        private void StopCallbacks()
        {
            _callbacks.CompleteAdding();
            if (_callbackThread != null && _callbackThread != Thread.CurrentThread && !_callbackThread.Join(TimeSpan.FromSeconds(10)))
            {
                Log.Error("TradeZeroBrokerage: callbacks still being delivered 10 s after Dispose");
            }
        }
    }
}
