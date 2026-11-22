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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using QuantConnect.Brokerages.TradeZero.Logging;
using QuantConnect.Logging;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.TradeZero.Api
{
    /// <summary>
    /// Error returned by the TradeZero API, or a failure to reach it
    /// </summary>
    public class TradeZeroApiException : Exception
    {
        /// <summary>HTTP status, null when no response was received</summary>
        public HttpStatusCode? StatusCode { get; }

        /// <summary>Raw response body (text or JSON)</summary>
        public string ResponseBody { get; }

        /// <summary>
        /// True when we cannot know whether the server processed the request (timeout, network error, 5xx).
        /// A POST /order in that state must be checked with GET /order/{clientOrderId} before any retry.
        /// </summary>
        public bool IsOutcomeUnknown { get; }

        public TradeZeroApiException(string message, HttpStatusCode? statusCode, string responseBody, bool isOutcomeUnknown, Exception inner = null)
            : base(message, inner)
        {
            StatusCode = statusCode;
            ResponseBody = responseBody;
            IsOutcomeUnknown = isOutcomeUnknown;
        }

        /// <summary>
        /// The server's error text, readable for a user (validation lines, or the JSON "detail"/"message")
        /// </summary>
        public string ServerMessage
        {
            get
            {
                if (string.IsNullOrWhiteSpace(ResponseBody))
                {
                    return Message;
                }
                try
                {
                    var error = JsonConvert.DeserializeObject<TzErrorBody>(ResponseBody);
                    if (error != null && (error.Detail != null || error.Message != null))
                    {
                        return string.Join(": ", new[] { error.Message, error.Detail }.Where(s => !string.IsNullOrEmpty(s)));
                    }
                }
                catch (JsonException)
                {
                    // plain text body
                }
                return ResponseBody.Trim();
            }
        }

        private class TzErrorBody
        {
            [JsonProperty("message")] public string Message { get; set; }
            [JsonProperty("detail")] public string Detail { get; set; }
        }
    }

    /// <summary>
    /// Synchronous client for the TradeZero REST API (https://webapi.tradezero.com/v1/api).
    /// Applies the documented per-endpoint rate limits and retries throttled (429) requests.
    /// </summary>
    public class TradeZeroRestClient : IDisposable
    {
        /// <summary>Production host (paper and live share it; the keys select the environment)</summary>
        public const string DefaultBaseUrl = "https://webapi.tradezero.com";

        private const string ApiPrefix = "/v1/api";
        private static readonly TimeSpan[] ThrottleBackoff = { TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6) };
        private static readonly TimeSpan[] TransientBackoff = { TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5) };

        private readonly HttpClient _http;
        private readonly Dictionary<string, RateGate> _gates;

        /// <summary>The API host this client talks to</summary>
        public Uri BaseUrl { get; }

        /// <param name="baseUrl">API host, e.g. https://webapi.tradezero.com or a tz-mock URL</param>
        /// <param name="keyId">TZ-API-KEY-ID</param>
        /// <param name="secret">TZ-API-SECRET-KEY</param>
        /// <param name="apiLogger">Exchange logger (secrets are scrubbed there)</param>
        /// <param name="timeout">Per-request timeout</param>
        /// <param name="transport">Optional HTTP transport, for tests</param>
        public TradeZeroRestClient(string baseUrl, string keyId, string secret, TradeZeroApiLogger apiLogger,
            TimeSpan? timeout = null, HttpMessageHandler transport = null)
        {
            if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(secret))
            {
                throw new ArgumentException("TradeZero API key id and secret are required");
            }
            BaseUrl = new Uri(string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.TrimEnd('/') + "/");

            var handler = new TradeZeroLoggingHandler(apiLogger ?? TradeZeroApiLogger.Disabled,
                transport ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
            _http = new HttpClient(handler) { BaseAddress = BaseUrl, Timeout = timeout ?? TimeSpan.FromSeconds(15) };
            _http.DefaultRequestHeaders.Add("TZ-API-KEY-ID", keyId);
            _http.DefaultRequestHeaders.Add("TZ-API-SECRET-KEY", secret);
            _http.DefaultRequestHeaders.Add("Accept", "application/json");

            // documented token buckets (developer.tradezero.com, Rate limits), per API key.
            // /pnl and /positions are documented at 3/s burst 3, but paper answered 429 to a second
            // /positions call 140 ms after the first (2026-11-20): they share one spaced-out gate.
            var portfolioGate = new RateGate(1, TimeSpan.FromMilliseconds(350));
            _gates = new Dictionary<string, RateGate>
            {
                ["accounts"] = new RateGate(2, TimeSpan.FromSeconds(1)),
                ["pnl"] = portfolioGate,
                ["positions"] = portfolioGate,
                ["orders"] = new RateGate(2, TimeSpan.FromSeconds(1)),
                ["order"] = new RateGate(2, TimeSpan.FromSeconds(1)),
                ["place"] = new RateGate(10, TimeSpan.FromSeconds(1)),
                ["cancel"] = new RateGate(15, TimeSpan.FromSeconds(1)),
                ["cancelAll"] = new RateGate(1, TimeSpan.FromMilliseconds(350)),
                ["etb"] = new RateGate(2, TimeSpan.FromSeconds(1)),
                ["routes"] = new RateGate(1, TimeSpan.FromSeconds(1)),
                // not documented: room for a history poll every 250 ms plus the quote/accept calls
                ["locates"] = new RateGate(5, TimeSpan.FromSeconds(1)),
            };
        }

        public List<TzAccount> GetAccounts()
        {
            return Parse<TzAccountsResponse>(SendOrThrow("accounts", HttpMethod.Get, "/accounts")).Accounts;
        }

        public TzAccount GetAccount(string accountId)
        {
            return Parse<TzAccount>(SendOrThrow("accounts", HttpMethod.Get, $"/account/{Esc(accountId)}"));
        }

        public TzPnl GetPnl(string accountId)
        {
            return Parse<TzPnl>(SendOrThrow("pnl", HttpMethod.Get, $"/accounts/{Esc(accountId)}/pnl"));
        }

        public List<TzPosition> GetPositions(string accountId)
        {
            return Parse<TzPositionsResponse>(SendOrThrow("positions", HttpMethod.Get, $"/accounts/{Esc(accountId)}/positions")).Positions;
        }

        /// <summary>
        /// Today's orders plus orders still working from earlier days
        /// </summary>
        public List<TzOrder> GetOrders(string accountId)
        {
            return Parse<TzOrdersResponse>(SendOrThrow("orders", HttpMethod.Get, $"/accounts/{Esc(accountId)}/orders")).Orders;
        }

        /// <summary>
        /// One order by client order id, null if the server does not know it
        /// </summary>
        public TzOrder GetOrder(string accountId, string clientOrderId)
        {
            var (status, body) = Send("order", HttpMethod.Get, $"/accounts/{Esc(accountId)}/order/{Esc(clientOrderId)}");
            if (status == HttpStatusCode.NotFound ||
                status == HttpStatusCode.BadRequest && body != null && body.Contains("Unable to find", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            EnsureSuccess(status, body, "GET order");
            return Parse<TzOrder>(body);
        }

        /// <summary>
        /// Places an order. The returned row may already be Rejected (synchronous rejections such as R114/R145).
        /// </summary>
        /// <exception cref="TradeZeroApiException">On a 400 validation error, or when the outcome is unknown</exception>
        public TzOrder PlaceOrder(string accountId, TzOrderRequest request)
        {
            var json = JsonConvert.SerializeObject(request);
            var (status, body) = Send("place", HttpMethod.Post, $"/accounts/{Esc(accountId)}/order",
                () => new StringContent(json, Encoding.UTF8, "application/json"));
            EnsureSuccess(status, body, "POST order");
            return Parse<TzOrder>(body);
        }

        /// <summary>
        /// Cancels one order. Returns the order row (Canceled, or unchanged with an R130 text when it was
        /// already final), or null if the server does not know the order.
        /// </summary>
        public TzOrder CancelOrder(string accountId, string clientOrderId)
        {
            var (status, body) = Send("cancel", HttpMethod.Delete, $"/accounts/{Esc(accountId)}/orders/{Esc(clientOrderId)}");
            if (status == HttpStatusCode.NotFound)
            {
                return null;
            }
            EnsureSuccess(status, body, "DELETE order");
            return Parse<TzOrder>(body);
        }

        /// <summary>
        /// Cancels all working orders of the account, optionally for one symbol
        /// </summary>
        public void CancelAllOrders(string accountId, string symbol = null)
        {
            var path = "/accounts/orders" + (string.IsNullOrEmpty(symbol) ? string.Empty : $"?symbol={Esc(symbol)}");
            var (status, body) = Send("cancelAll", HttpMethod.Delete, path,
                () => new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("account", accountId) }));
            EnsureSuccess(status, body, "DELETE all orders");
        }

        public bool IsEasyToBorrow(string accountId, string symbol)
        {
            return Parse<TzEasyToBorrowResponse>(SendOrThrow("etb", HttpMethod.Get,
                $"/accounts/{Esc(accountId)}/is-easy-to-borrow/symbol/{Esc(symbol)}")).IsEasyToBorrow;
        }

        public List<TzRoute> GetRoutes(string accountId)
        {
            return Parse<TzRoutesResponse>(SendOrThrow("routes", HttpMethod.Get, $"/accounts/{Esc(accountId)}/routes")).Routes;
        }

        /// <summary>
        /// Asks for a locate quote. The answer comes as history rows (<see cref="GetLocateHistory"/>) with this id,
        /// and id + ".SU" for the SingleUse offer of a Reg SHO symbol.
        /// </summary>
        /// <exception cref="TradeZeroApiException">400 (quantity below 100, reused id R114), or outcome unknown</exception>
        public void RequestLocateQuote(string accountId, string symbol, long quantity, string quoteReqId)
        {
            PostJson("POST locate quote", "/accounts/locates/quote",
                new TzLocateQuoteRequest { Account = accountId, Symbol = symbol, Quantity = quantity, QuoteReqId = quoteReqId });
        }

        /// <summary>
        /// Accepts an offered row (base id or id + ".SU"). TradeZero always acknowledges, even an unknown id:
        /// the history row reaching 50 (Filled) is the only proof. Re-sending is documented as safe.
        /// </summary>
        public void AcceptLocate(string accountId, string quoteReqId)
        {
            PostJson("POST locate accept", "/accounts/locates/accept",
                new TzLocateAcceptRequest { AccountId = accountId, QuoteReqId = quoteReqId });
        }

        /// <summary>
        /// Cancels an offered row, or a queued sell-back
        /// </summary>
        public void CancelLocate(string accountId, string quoteReqId)
        {
            var (status, body) = Send("locates", HttpMethod.Delete, $"/accounts/locates/cancel/accounts/{Esc(accountId)}/quoteReqID/{Esc(quoteReqId)}");
            EnsureSuccess(status, body, "DELETE locate");
        }

        /// <summary>
        /// Sells located shares of one type back for credit. The result comes as a history row with this id.
        /// </summary>
        /// <param name="locateType">"Locate", "PreBorrow" or "SingleUse"</param>
        public void SellLocate(string accountId, string symbol, long quantity, string locateType, string quoteReqId)
        {
            PostJson("POST locate sell", "/accounts/locates/sell",
                new TzLocateSellRequest { Account = accountId, Symbol = symbol, Quantity = quantity, LocateType = locateType, QuoteReqId = quoteReqId });
        }

        /// <summary>
        /// Today's locate quotes, accepts and sell-backs
        /// </summary>
        public List<TzLocateHistoryRow> GetLocateHistory(string accountId)
        {
            var body = SendOrThrow("locates", HttpMethod.Get, $"/accounts/{Esc(accountId)}/locates/history");
            return ParseList<TzLocateHistoryRow>(body, "locateHistory");
        }

        /// <summary>
        /// Located shares per (symbol, locateType)
        /// </summary>
        public List<TzLocateInventoryRow> GetLocateInventory(string accountId)
        {
            var body = SendOrThrow("locates", HttpMethod.Get, $"/accounts/{Esc(accountId)}/locates/inventory");
            return ParseList<TzLocateInventoryRow>(body, "locateInventory");
        }

        private void PostJson(string what, string path, object payload)
        {
            var json = JsonConvert.SerializeObject(payload);
            var (status, body) = Send("locates", HttpMethod.Post, path, () => new StringContent(json, Encoding.UTF8, "application/json"));
            EnsureSuccess(status, body, what);
        }

        /// <summary>
        /// Accepts both <c>{"key": [...]}</c> and a bare list
        /// </summary>
        private static List<T> ParseList<T>(string body, string key)
        {
            try
            {
                var token = Newtonsoft.Json.Linq.JToken.Parse(string.IsNullOrWhiteSpace(body) ? "[]" : body);
                if (token is Newtonsoft.Json.Linq.JObject obj)
                {
                    token = obj[key] ?? new Newtonsoft.Json.Linq.JArray();
                }
                return token.Type == Newtonsoft.Json.Linq.JTokenType.Null ? new List<T>() : token.ToObject<List<T>>() ?? new List<T>();
            }
            catch (JsonException e)
            {
                throw new TradeZeroApiException($"Unexpected response for {key}: {e.Message}", HttpStatusCode.OK, body, false, e);
            }
        }

        public void Dispose()
        {
            _http.Dispose();
            foreach (var gate in _gates.Values.Distinct())
            {
                gate.Dispose();
            }
        }

        private string SendOrThrow(string gate, HttpMethod method, string path)
        {
            var (status, body) = Send(gate, method, path);
            EnsureSuccess(status, body, $"{method} {path}");
            return body;
        }

        /// <summary>
        /// Sends a request with rate limiting and retries:
        /// - 429 (request not processed): retried for every method, with backoff;
        /// - network errors, timeouts and 5xx: retried for GET only; other methods raise an
        ///   exception with <see cref="TradeZeroApiException.IsOutcomeUnknown"/> set.
        /// </summary>
        private (HttpStatusCode Status, string Body) Send(string gate, HttpMethod method, string path, Func<HttpContent> content = null)
        {
            var isGet = method == HttpMethod.Get;
            var throttled = 0;
            var transient = 0;
            while (true)
            {
                _gates[gate].WaitToProceed();
                HttpResponseMessage response;
                try
                {
                    using var request = new HttpRequestMessage(method, ApiPrefix.TrimStart('/') + path) { Content = content?.Invoke() };
                    response = _http.Send(request);
                }
                catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is OperationCanceledException || e is IOException)
                {
                    if (isGet && transient < TransientBackoff.Length)
                    {
                        Thread.Sleep(TransientBackoff[transient++]);
                        continue;
                    }
                    throw new TradeZeroApiException($"{method} {path}: {e.GetType().Name}: {e.Message}", null, null, isOutcomeUnknown: !isGet, e);
                }

                using (response)
                {
                    var body = response.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                    var status = response.StatusCode;
                    if (status == HttpStatusCode.TooManyRequests && throttled < ThrottleBackoff.Length)
                    {
                        Log.Trace($"TradeZeroRestClient: {method} {path} throttled (429), retrying in {ThrottleBackoff[throttled].TotalSeconds}s");
                        Thread.Sleep(ThrottleBackoff[throttled++]);
                        continue;
                    }
                    if ((int)status >= 500)
                    {
                        if (isGet && transient < TransientBackoff.Length)
                        {
                            Thread.Sleep(TransientBackoff[transient++]);
                            continue;
                        }
                        throw new TradeZeroApiException($"{method} {path}: HTTP {(int)status}", status, body, isOutcomeUnknown: !isGet);
                    }
                    return (status, body);
                }
            }
        }

        private static void EnsureSuccess(HttpStatusCode status, string body, string what)
        {
            if ((int)status < 200 || (int)status >= 300)
            {
                var hint = status == HttpStatusCode.NotFound && string.Equals(body?.Trim(), "Not found", StringComparison.Ordinal)
                    ? " (TradeZero answers a bare 404 for missing/invalid API keys)"
                    : string.Empty;
                throw new TradeZeroApiException($"{what}: HTTP {(int)status}{hint}", status, body, isOutcomeUnknown: false);
            }
        }

        private static T Parse<T>(string body)
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(body ?? string.Empty)
                    ?? throw new TradeZeroApiException($"Empty response for {typeof(T).Name}", HttpStatusCode.OK, body, false);
            }
            catch (JsonException e)
            {
                throw new TradeZeroApiException($"Unexpected response for {typeof(T).Name}: {e.Message}", HttpStatusCode.OK, body, false, e);
            }
        }

        private static string Esc(string value)
        {
            return Uri.EscapeDataString(value ?? string.Empty);
        }
    }
}
