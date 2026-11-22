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
using System.Globalization;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace QuantConnect.Brokerages.TradeZero.Tests
{
    /// <summary>
    /// Client for tz-mock's control endpoints (/mock). Each API key is an isolated session.
    /// The mock URL comes from TZ_MOCK_URL (the dev container sets http://tzmock:8000).
    /// </summary>
    public static class TzMock
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private static bool? _available;

        public static string Url => (Environment.GetEnvironmentVariable("TZ_MOCK_URL") ?? "http://localhost:8000").TrimEnd('/');

        /// <summary>
        /// Skips the test (Inconclusive) when tz-mock is not running
        /// </summary>
        public static void RequireAvailable()
        {
            if (_available == null)
            {
                try
                {
                    _available = Http.GetAsync($"{Url}/mock/scenarios").GetAwaiter().GetResult().IsSuccessStatusCode;
                }
                catch (Exception)
                {
                    _available = false;
                }
            }
            if (_available == false)
            {
                Assert.Ignore($"tz-mock is not reachable at {Url} (set TZ_MOCK_URL, or start it: docker compose -f .devcontainer/docker-compose.yml up -d tzmock)");
            }
        }

        /// <summary>
        /// A fresh session key
        /// </summary>
        public static string NewKey(string prefix = "test")
        {
            return $"{prefix}-{Guid.NewGuid():N}";
        }

        public static JObject SetScenario(string key, string name, object parameters = null)
        {
            var body = JsonConvert.SerializeObject(new { name, @params = parameters ?? new { } });
            return Send(key, HttpMethod.Post, "/mock/scenario", body);
        }

        public static JObject AdvanceTo(string key, DateTime utc)
        {
            var body = JsonConvert.SerializeObject(new { to = utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) });
            return Send(key, HttpMethod.Post, "/mock/time", body);
        }

        public static JObject AdvanceBy(string key, TimeSpan delta)
        {
            return AdvanceTo(key, Now(key) + delta);
        }

        public static JObject State(string key)
        {
            return Send(key, HttpMethod.Get, "/mock/state", null);
        }

        public static DateTime Now(string key)
        {
            return DateTime.Parse(State(key)["now"]!.Value<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        }

        public static decimal Price(string key, string ticker)
        {
            return State(key)["prices"]![ticker]!.Value<decimal>();
        }

        /// <summary>The session's locate state: inventory, openQuotes, inUse, supply, locateCosts, locateCredits...</summary>
        public static JObject Locates(string key)
        {
            return (JObject)State(key)["locates"];
        }

        /// <summary>Sets one inventory row's available shares (no quote, no cost)</summary>
        public static JObject SetInventory(string key, string symbol, int available, string locateType = "Locate")
        {
            return Send(key, HttpMethod.Post, "/mock/locates/inventory", JsonConvert.SerializeObject(new { symbol, available, locateType }));
        }

        /// <summary>The next quote(s) for the symbol are refused with this row</summary>
        public static JObject NextLocateError(string key, string symbol, int code, string text, int status = 56, int count = 1)
        {
            return Send(key, HttpMethod.Post, "/mock/locates/next-error", JsonConvert.SerializeObject(new { symbol, code, text, status, count }));
        }

        /// <summary>Fails the next matching /v1/api request(s); when "after", the request is processed and only the response is lost</summary>
        public static JObject AddFault(string key, string method, string path, int status = 500, int count = 1, string when = "before")
        {
            return Send(key, HttpMethod.Post, "/mock/faults", JsonConvert.SerializeObject(new { method, path, status, count, when }));
        }

        private static JObject Send(string key, HttpMethod method, string path, string body)
        {
            using var request = new HttpRequestMessage(method, Url + path);
            request.Headers.Add("TZ-API-KEY-ID", key);
            if (body != null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            using var response = Http.Send(request);
            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"tz-mock {method} {path}: HTTP {(int)response.StatusCode} {text}");
            }
            return JObject.Parse(text);
        }
    }
}
