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
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.TradeZero
{
    /// <summary>
    /// Brokerage model for TradeZero: US equities, long and short (easy to borrow only), whole shares.
    /// </summary>
    /// <remarks>
    /// In live trading the TradeZero factory sets it as the algorithm's default model. In backtests:
    /// <code>
    /// AddReference("QuantConnect.Brokerages.TradeZero")
    /// from QuantConnect.Brokerages.TradeZero import TradeZeroBrokerageModel
    /// self.set_brokerage_model(TradeZeroBrokerageModel())
    /// </code>
    /// Margin accounts use Lean's pattern day trading model: intraday leverage while the market is open,
    /// overnight leverage otherwise (defaults 4x / 2x, configurable).
    /// </remarks>
    public class TradeZeroBrokerageModel : DefaultBrokerageModel
    {
        private static readonly IReadOnlyDictionary<SecurityType, string> Markets = new Dictionary<SecurityType, string>
        {
            { SecurityType.Equity, Market.USA }
        };

        private readonly IFeeModel _feeModel;

        /// <summary>Leverage while the market is open (margin accounts)</summary>
        public decimal IntradayLeverage { get; }

        /// <summary>Leverage while the market is closed (margin accounts)</summary>
        public decimal OvernightLeverage { get; }

        /// <summary>
        /// The broker's own margin figures for the connected account, null when not connected (e.g. backtests)
        /// </summary>
        public TradeZeroAccount Account => TradeZeroAccount.Current;

        public override IReadOnlyDictionary<SecurityType, string> DefaultMarkets => Markets;

        public TradeZeroBrokerageModel(AccountType accountType = AccountType.Margin, decimal intradayLeverage = 4m,
            decimal overnightLeverage = 2m, IFeeModel feeModel = null)
            : base(accountType)
        {
            if (intradayLeverage < 1 || overnightLeverage < 1)
            {
                throw new ArgumentException("TradeZero leverage must be at least 1");
            }
            IntradayLeverage = intradayLeverage;
            OvernightLeverage = overnightLeverage;
            _feeModel = feeModel ?? new TradeZeroFeeModel();
        }

        public override bool CanSubmitOrder(Security security, Order order, out BrokerageMessageEvent message)
        {
            message = null;
            if (security.Type != SecurityType.Equity)
            {
                message = new BrokerageMessageEvent(BrokerageMessageType.Warning, "NotSupported",
                    $"TradeZero brokerage only supports US equities, not {security.Type}.");
                return false;
            }
            var error = TradeZeroOrderMapper.Validate(order);
            if (error != null)
            {
                message = new BrokerageMessageEvent(BrokerageMessageType.Warning, "NotSupported", error);
                return false;
            }
            return true;
        }

        public override bool CanUpdateOrder(Security security, Order order, UpdateOrderRequest request, out BrokerageMessageEvent message)
        {
            message = null;
            if (order.Type != OrderType.Limit && order.Type != OrderType.StopMarket && order.Type != OrderType.StopLimit)
            {
                message = new BrokerageMessageEvent(BrokerageMessageType.Warning, "NotSupported",
                    $"TradeZero orders are updated by cancel-then-replace, which is only done for Limit, StopMarket and StopLimit orders (not {order.Type}).");
                return false;
            }
            if (request.Quantity.HasValue)
            {
                var quantity = Math.Abs(request.Quantity.Value);
                if (quantity != Math.Truncate(quantity) || quantity == 0 || quantity > TradeZeroOrderMapper.MaxQuantity)
                {
                    message = new BrokerageMessageEvent(BrokerageMessageType.Warning, "NotSupported",
                        $"Invalid quantity for TradeZero: {request.Quantity.Value} (whole shares, at most {TradeZeroOrderMapper.MaxQuantity:N0}).");
                    return false;
                }
            }
            return true;
        }

        public override IFeeModel GetFeeModel(Security security) => _feeModel;

        public override decimal GetLeverage(Security security)
        {
            if (AccountType == AccountType.Cash || security.Type != SecurityType.Equity)
            {
                return 1m;
            }
            return OvernightLeverage;
        }

        public override IBuyingPowerModel GetBuyingPowerModel(Security security)
        {
            if (AccountType == AccountType.Margin && security?.Type == SecurityType.Equity)
            {
                return new PatternDayTradingMarginModel(OvernightLeverage, IntradayLeverage);
            }
            return base.GetBuyingPowerModel(security);
        }

        public override IShortableProvider GetShortableProvider(Security security) => TradeZeroShortableProvider.Instance;
    }
}
