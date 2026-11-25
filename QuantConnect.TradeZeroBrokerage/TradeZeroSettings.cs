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
using System.Globalization;
using QuantConnect.Brokerages.TradeZero.Api;

namespace QuantConnect.Brokerages.TradeZero
{
    /// <summary>
    /// Which kind of TradeZero account the brokerage is allowed to connect to
    /// </summary>
    public enum TradeZeroEnvironment
    {
        /// <summary>Only a paper account (accountType "Paper"): the default</summary>
        Paper,
        /// <summary>Only a live (non-paper) account</summary>
        Live
    }

    /// <summary>
    /// TradeZero brokerage settings and their configuration keys
    /// </summary>
    public class TradeZeroSettings
    {
        // configuration keys (config.json / Lean launcher arguments)
        public const string ApiUrlKey = "tradezero-api-url";
        public const string ApiKeyIdKey = "tradezero-api-key-id";
        public const string ApiSecretKeyKey = "tradezero-api-secret-key";
        public const string AccountIdKey = "tradezero-account-id";
        public const string EnvironmentKey = "tradezero-environment";
        public const string RouteKey = "tradezero-route";
        public const string ApiLogFileKey = "tradezero-api-log-file";
        public const string ApiLogMaxMbKey = "tradezero-api-log-max-mb";
        public const string ApiLogBodiesKey = "tradezero-api-log-bodies";
        public const string ActivePollMsKey = "tradezero-poll-active-ms";
        public const string IdlePollMsKey = "tradezero-poll-idle-ms";
        public const string AccountPollMsKey = "tradezero-poll-account-ms";
        public const string ReconcileSecondsKey = "tradezero-reconcile-seconds";
        public const string FeePerOrderKey = "tradezero-fee-per-order";
        public const string FeePerShareKey = "tradezero-fee-per-share";
        public const string AccountTypeKey = "tradezero-account-type";
        public const string IntradayLeverageKey = "tradezero-intraday-leverage";
        public const string OvernightLeverageKey = "tradezero-overnight-leverage";
        public const string LocateTimeoutSecondsKey = "tradezero-locate-timeout-seconds";
        public const string LocatePollMsKey = "tradezero-locate-poll-ms";

        /// <summary>Environment variables used when the key id / secret are not configured</summary>
        public const string ApiKeyIdEnvironmentVariable = "TZ_API_KEY_ID";
        public const string ApiSecretKeyEnvironmentVariable = "TZ_API_SECRET_KEY";

        /// <summary>All configuration keys, in documentation order</summary>
        public static readonly string[] AllKeys =
        {
            ApiUrlKey, ApiKeyIdKey, ApiSecretKeyKey, AccountIdKey, EnvironmentKey, RouteKey,
            ApiLogFileKey, ApiLogMaxMbKey, ApiLogBodiesKey,
            ActivePollMsKey, IdlePollMsKey, AccountPollMsKey, ReconcileSecondsKey,
            FeePerOrderKey, FeePerShareKey, AccountTypeKey, IntradayLeverageKey, OvernightLeverageKey,
            LocateTimeoutSecondsKey, LocatePollMsKey
        };

        public string ApiUrl { get; set; } = TradeZeroRestClient.DefaultBaseUrl;
        public string ApiKeyId { get; set; }
        public string ApiSecretKey { get; set; }
        /// <summary>Account number; may be empty when the keys give access to a single account</summary>
        public string AccountId { get; set; }
        public TradeZeroEnvironment Environment { get; set; } = TradeZeroEnvironment.Paper;
        /// <summary>Route for orders without one in their properties. Empty: none on paper, SMART (or the first stock route) on live.</summary>
        public string Route { get; set; }

        /// <summary>JSON-lines file recording all API exchanges. Empty: disabled.</summary>
        public string ApiLogFile { get; set; }
        public int ApiLogMaxMb { get; set; } = 50;
        public bool ApiLogBodies { get; set; } = true;

        /// <summary>Order polling interval while this algorithm has working orders</summary>
        public TimeSpan ActivePollInterval { get; set; } = TimeSpan.FromSeconds(1);
        /// <summary>Order polling interval without working orders</summary>
        public TimeSpan IdlePollInterval { get; set; } = TimeSpan.FromSeconds(5);
        /// <summary>P&amp;L / account polling interval (GET /account runs every 6th time)</summary>
        public TimeSpan AccountPollInterval { get; set; } = TimeSpan.FromSeconds(5);
        /// <summary>Interval of the position check against Lean's holdings</summary>
        public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromSeconds(30);
        /// <summary>How long an update waits for the cancel of the order it replaces</summary>
        public TimeSpan ReplaceCancelTimeout { get; set; } = TimeSpan.FromSeconds(5);
        /// <summary>
        /// Longest a locate workflow waits for TradeZero (quote answer, then the fill of the accepted offer), not counting the
        /// time spent in the locate handler. Also bounds sell-backs.
        /// </summary>
        public TimeSpan LocateTimeout { get; set; } = TimeSpan.FromSeconds(20);
        /// <summary>How often the locate history is read while a locate workflow waits</summary>
        public TimeSpan LocatePollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

        public decimal FeePerOrder { get; set; }
        public decimal FeePerShare { get; set; }

        /// <summary>Intraday leverage used by the brokerage model (checked against the account's allowed leverage at connect)</summary>
        public decimal IntradayLeverage { get; set; } = 4m;

        /// <summary>
        /// Reads settings from brokerage data (config values), falling back to the TZ_API_KEY_ID /
        /// TZ_API_SECRET_KEY environment variables for the keys.
        /// </summary>
        public static TradeZeroSettings FromBrokerageData(IReadOnlyDictionary<string, string> data, Func<string, string> getEnvironmentVariable = null)
        {
            getEnvironmentVariable ??= System.Environment.GetEnvironmentVariable;
            string Get(string key) => data != null && data.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

            var settings = new TradeZeroSettings
            {
                ApiUrl = Get(ApiUrlKey) ?? TradeZeroRestClient.DefaultBaseUrl,
                ApiKeyId = Get(ApiKeyIdKey) ?? getEnvironmentVariable(ApiKeyIdEnvironmentVariable),
                ApiSecretKey = Get(ApiSecretKeyKey) ?? getEnvironmentVariable(ApiSecretKeyEnvironmentVariable),
                AccountId = Get(AccountIdKey),
                Environment = ParseEnvironment(Get(EnvironmentKey)),
                Route = Get(RouteKey),
                ApiLogFile = Get(ApiLogFileKey),
            };
            settings.ApiLogMaxMb = ParseInt(Get(ApiLogMaxMbKey), settings.ApiLogMaxMb, ApiLogMaxMbKey);
            settings.ApiLogBodies = ParseBool(Get(ApiLogBodiesKey), settings.ApiLogBodies, ApiLogBodiesKey);
            settings.ActivePollInterval = TimeSpan.FromMilliseconds(Math.Max(500, ParseInt(Get(ActivePollMsKey), 1000, ActivePollMsKey)));
            settings.IdlePollInterval = TimeSpan.FromMilliseconds(Math.Max(500, ParseInt(Get(IdlePollMsKey), 5000, IdlePollMsKey)));
            settings.AccountPollInterval = TimeSpan.FromMilliseconds(Math.Max(1000, ParseInt(Get(AccountPollMsKey), 5000, AccountPollMsKey)));
            settings.ReconcileInterval = TimeSpan.FromSeconds(Math.Max(5, ParseInt(Get(ReconcileSecondsKey), 30, ReconcileSecondsKey)));
            settings.LocateTimeout = TimeSpan.FromSeconds(Math.Max(1, ParseInt(Get(LocateTimeoutSecondsKey), 20, LocateTimeoutSecondsKey)));
            settings.LocatePollInterval = TimeSpan.FromMilliseconds(Math.Max(100, ParseInt(Get(LocatePollMsKey), 250, LocatePollMsKey)));
            settings.FeePerOrder = ParseDecimal(Get(FeePerOrderKey), 0m, FeePerOrderKey);
            settings.FeePerShare = ParseDecimal(Get(FeePerShareKey), 0m, FeePerShareKey);
            settings.IntradayLeverage = ParseAccountType(Get(AccountTypeKey)) == AccountType.Cash
                ? 1m
                : ParseDecimal(Get(IntradayLeverageKey), 4m, IntradayLeverageKey);
            return settings;
        }

        /// <summary>
        /// "paper" (default) or "live"
        /// </summary>
        public static TradeZeroEnvironment ParseEnvironment(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("paper", StringComparison.OrdinalIgnoreCase))
            {
                return TradeZeroEnvironment.Paper;
            }
            if (value.Trim().Equals("live", StringComparison.OrdinalIgnoreCase))
            {
                return TradeZeroEnvironment.Live;
            }
            throw new ArgumentException($"Invalid {EnvironmentKey} '{value}': expected 'paper' or 'live'");
        }

        /// <summary>
        /// "margin" (default) or "cash"
        /// </summary>
        public static AccountType ParseAccountType(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("margin", StringComparison.OrdinalIgnoreCase))
            {
                return AccountType.Margin;
            }
            if (value.Trim().Equals("cash", StringComparison.OrdinalIgnoreCase))
            {
                return AccountType.Cash;
            }
            throw new ArgumentException($"Invalid {AccountTypeKey} '{value}': expected 'margin' or 'cash'");
        }

        internal static int ParseInt(string value, int defaultValue, string key)
        {
            if (value == null)
            {
                return defaultValue;
            }
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                ? result
                : throw new ArgumentException($"Invalid {key} '{value}': expected an integer");
        }

        internal static decimal ParseDecimal(string value, decimal defaultValue, string key)
        {
            if (value == null)
            {
                return defaultValue;
            }
            return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result)
                ? result
                : throw new ArgumentException($"Invalid {key} '{value}': expected a number");
        }

        internal static bool ParseBool(string value, bool defaultValue, string key)
        {
            if (value == null)
            {
                return defaultValue;
            }
            return bool.TryParse(value, out var result)
                ? result
                : throw new ArgumentException($"Invalid {key} '{value}': expected true or false");
        }
    }
}
