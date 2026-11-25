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
using System.Linq;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Packets;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.TradeZero
{
    /// <summary>
    /// Creates the TradeZero brokerage and its brokerage model from Lean configuration.
    /// Select it with "live-mode-brokerage": "TradeZeroBrokerage".
    /// </summary>
    public class TradeZeroBrokerageFactory : BrokerageFactory
    {
        /// <summary>
        /// All tradezero-* configuration values (keys may also come from TZ_API_KEY_ID / TZ_API_SECRET_KEY)
        /// </summary>
        public override Dictionary<string, string> BrokerageData =>
            TradeZeroSettings.AllKeys.ToDictionary(key => key, key => Config.Get(key));

        public TradeZeroBrokerageFactory() : base(typeof(TradeZeroBrokerage))
        {
        }

        /// <summary>
        /// The default brokerage model for algorithms trading through TradeZero
        /// </summary>
        public override IBrokerageModel GetBrokerageModel(IOrderProvider orderProvider)
        {
            return CreateBrokerageModel(BrokerageData);
        }

        /// <summary>
        /// Builds the brokerage model from configuration values
        /// </summary>
        public static TradeZeroBrokerageModel CreateBrokerageModel(IReadOnlyDictionary<string, string> data)
        {
            string Get(string key) => data.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
            var accountType = TradeZeroSettings.ParseAccountType(Get(TradeZeroSettings.AccountTypeKey));
            var intraday = TradeZeroSettings.ParseDecimal(Get(TradeZeroSettings.IntradayLeverageKey), 4m, TradeZeroSettings.IntradayLeverageKey);
            var overnight = TradeZeroSettings.ParseDecimal(Get(TradeZeroSettings.OvernightLeverageKey), 2m, TradeZeroSettings.OvernightLeverageKey);
            var fees = new TradeZeroFeeModel(
                TradeZeroSettings.ParseDecimal(Get(TradeZeroSettings.FeePerOrderKey), 0m, TradeZeroSettings.FeePerOrderKey),
                TradeZeroSettings.ParseDecimal(Get(TradeZeroSettings.FeePerShareKey), 0m, TradeZeroSettings.FeePerShareKey));
            return new TradeZeroBrokerageModel(accountType, intraday, overnight, fees);
        }

        public override IBrokerage CreateBrokerage(LiveNodePacket job, IAlgorithm algorithm)
        {
            var settings = TradeZeroSettings.FromBrokerageData(job.BrokerageData);
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(settings.ApiKeyId))
            {
                errors.Add($"{TradeZeroSettings.ApiKeyIdKey} (or env {TradeZeroSettings.ApiKeyIdEnvironmentVariable})");
            }
            if (string.IsNullOrWhiteSpace(settings.ApiSecretKey))
            {
                errors.Add($"{TradeZeroSettings.ApiSecretKeyKey} (or env {TradeZeroSettings.ApiSecretKeyEnvironmentVariable})");
            }
            if (errors.Count > 0)
            {
                throw new ArgumentException($"TradeZeroBrokerageFactory: missing configuration: {string.Join(", ", errors)}");
            }
            return new TradeZeroBrokerage(settings, algorithm.Transactions, algorithm.Portfolio);
        }

        public override void Dispose()
        {
        }
    }
}
