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
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace QuantConnect.Brokerages.TradeZero.Logging
{
    /// <summary>
    /// HTTP pipeline stage that records every request/response with <see cref="TradeZeroApiLogger"/>.
    /// Being part of the HttpClient pipeline, no API call can bypass it. Headers are never read.
    /// </summary>
    public class TradeZeroLoggingHandler : DelegatingHandler
    {
        private readonly TradeZeroApiLogger _logger;

        public TradeZeroLoggingHandler(TradeZeroApiLogger logger, HttpMessageHandler innerHandler) : base(innerHandler)
        {
            _logger = logger;
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestBody = ReadRequest(request);
            var watch = Stopwatch.StartNew();
            try
            {
                var response = base.Send(request, cancellationToken);
                if (_logger.Enabled)
                {
                    response.Content?.LoadIntoBufferAsync().GetAwaiter().GetResult();
                    var body = response.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
                    _logger.LogHttp(request.Method.Method, request.RequestUri?.PathAndQuery, requestBody, (int)response.StatusCode, body, watch.ElapsedMilliseconds);
                }
                return response;
            }
            catch (Exception e)
            {
                _logger.LogHttp(request.Method.Method, request.RequestUri?.PathAndQuery, requestBody, null, null, watch.ElapsedMilliseconds, $"{e.GetType().Name}: {e.Message}");
                throw;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestBody = ReadRequest(request);
            var watch = Stopwatch.StartNew();
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (_logger.Enabled)
                {
                    if (response.Content != null)
                    {
                        await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                    }
                    var body = response.Content == null ? null : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    _logger.LogHttp(request.Method.Method, request.RequestUri?.PathAndQuery, requestBody, (int)response.StatusCode, body, watch.ElapsedMilliseconds);
                }
                return response;
            }
            catch (Exception e)
            {
                _logger.LogHttp(request.Method.Method, request.RequestUri?.PathAndQuery, requestBody, null, null, watch.ElapsedMilliseconds, $"{e.GetType().Name}: {e.Message}");
                throw;
            }
        }

        private string ReadRequest(HttpRequestMessage request)
        {
            if (!_logger.Enabled || request.Content == null)
            {
                return null;
            }
            // our request contents are StringContent / FormUrlEncodedContent: buffered, safe to read twice
            return request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
    }
}
