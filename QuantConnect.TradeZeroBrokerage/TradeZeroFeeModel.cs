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
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.TradeZero
{
    /// <summary>
    /// Configurable TradeZero commission model: a fixed amount per order plus an amount per share.
    /// Defaults to zero. The TradeZero API does not report commissions per fill, so live fills are
    /// charged with this same model (the per-order part on the order's first fill).
    /// </summary>
    public class TradeZeroFeeModel : FeeModel
    {
        /// <summary>USD charged once per order</summary>
        public decimal PerOrder { get; }

        /// <summary>USD charged per share filled</summary>
        public decimal PerShare { get; }

        public TradeZeroFeeModel(decimal perOrder = 0m, decimal perShare = 0m)
        {
            if (perOrder < 0 || perShare < 0)
            {
                throw new ArgumentException("TradeZero fees cannot be negative");
            }
            PerOrder = perOrder;
            PerShare = perShare;
        }

        public override OrderFee GetOrderFee(OrderFeeParameters parameters)
        {
            return new OrderFee(new CashAmount(PerOrder + PerShare * Math.Abs(parameters.Order.Quantity), Currencies.USD));
        }

        /// <summary>
        /// Fee for one live fill
        /// </summary>
        /// <param name="fillQuantity">Shares in this fill</param>
        /// <param name="isFirstFill">True for the first fill of the Lean order: it carries <see cref="PerOrder"/></param>
        public OrderFee GetFillFee(decimal fillQuantity, bool isFirstFill)
        {
            var amount = PerShare * Math.Abs(fillQuantity) + (isFirstFill ? PerOrder : 0m);
            return amount == 0m ? OrderFee.Zero : new OrderFee(new CashAmount(amount, Currencies.USD));
        }
    }
}
