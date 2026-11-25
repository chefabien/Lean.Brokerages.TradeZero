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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Brokerages.TradeZero.Api;
using QuantConnect.Brokerages.TradeZero.Logging;

namespace QuantConnect.Brokerages.TradeZero.Tests
{
    [TestFixture]
    public class TradeZeroApiLoggerTests
    {
        private const string KeyId = "KEYID-6f1c2b9a7d";
        private const string Secret = "SECRET-93be41d0aa77";

        private string _path;

        [SetUp]
        public void SetUp()
        {
            _path = Path.Combine(Path.GetTempPath(), $"tz-api-{Guid.NewGuid():N}.jsonl");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
            {
                File.Delete(file);
            }
        }

        [Test]
        public void HttpExchangesAreLoggedWithoutKeys()
        {
            // the server echoes the secret in its answer: it must still be masked
            var transport = new FakeTransport(_ => (HttpStatusCode.OK, $"{{\"accounts\":[{{\"account\":\"TZP1\",\"accountType\":\"Paper\",\"echo\":\"{Secret}\"}}]}}"));
            using (var logger = new TradeZeroApiLogger(_path, new[] { KeyId, Secret }))
            using (var client = new TradeZeroRestClient("http://example.test", KeyId, Secret, logger, transport: transport))
            {
                Assert.AreEqual("TZP1", client.GetAccounts().Single().Account);
                client.PlaceOrder("TZP1", new TzOrderRequest { ClientOrderId = "c1", Symbol = "AAPL", Side = "Buy", OpenClose = "Open", OrderType = "Market", OrderQuantity = 1, TimeInForce = "Day" });
                logger.LogEvent("lean", $"something mentioning {KeyId}");
                logger.Flush(TimeSpan.FromSeconds(2));
            }

            var text = File.ReadAllText(_path);
            StringAssert.DoesNotContain(KeyId, text);
            StringAssert.DoesNotContain(Secret, text);
            StringAssert.DoesNotContain("TZ-API", text);

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(JObject.Parse).ToList();
            Assert.AreEqual(3, lines.Count);
            Assert.AreEqual("GET", lines[0]["method"]!.Value<string>());
            Assert.AreEqual("/v1/api/accounts", lines[0]["path"]!.Value<string>());
            Assert.AreEqual(200, lines[0]["status"]!.Value<int>());
            Assert.AreEqual("***", lines[0]["response"]!["accounts"]![0]!["echo"]!.Value<string>());
            Assert.AreEqual("POST", lines[1]["method"]!.Value<string>());
            Assert.AreEqual("AAPL", lines[1]["request"]!["symbol"]!.Value<string>());
            Assert.AreEqual("lean", lines[2]["kind"]!.Value<string>());
        }

        [Test]
        public void RepeatedPollResponsesAreCollapsed()
        {
            var answers = new[] { "{\"orders\":[]}", "{\"orders\":[]}", "{\"orders\":[]}", "{\"orders\":[{\"clientOrderId\":\"a\"}]}" };
            var calls = 0;
            var transport = new FakeTransport(_ => (HttpStatusCode.OK, answers[Math.Min(calls++, answers.Length - 1)]));
            using (var logger = new TradeZeroApiLogger(_path, new[] { KeyId, Secret }))
            using (var client = new TradeZeroRestClient("http://example.test", KeyId, Secret, logger, transport: transport))
            {
                for (var i = 0; i < 4; i++)
                {
                    client.GetOrders("TZP1");
                }
                logger.Flush(TimeSpan.FromSeconds(2));
            }
            var lines = File.ReadAllLines(_path).Select(JObject.Parse).ToList();
            Assert.AreEqual(2, lines.Count);
            Assert.AreEqual(2, lines[1]["unchangedBefore"]!.Value<int>());
        }

        [Test]
        public void ErrorsAndUnknownOutcomesAreLogged()
        {
            var transport = new FakeTransport(request => request.Method == HttpMethod.Post
                ? throw new HttpRequestException("connection reset")
                : (HttpStatusCode.BadRequest, "- at '/symbol': 'AA PL' does not match pattern"));
            using (var logger = new TradeZeroApiLogger(_path, new[] { KeyId, Secret }))
            using (var client = new TradeZeroRestClient("http://example.test", KeyId, Secret, logger, transport: transport))
            {
                var unknown = Assert.Throws<TradeZeroApiException>(() => client.PlaceOrder("TZP1", new TzOrderRequest { Symbol = "AAPL" }));
                Assert.IsTrue(unknown.IsOutcomeUnknown);
                var invalid = Assert.Throws<TradeZeroApiException>(() => client.GetPositions("TZP1"));
                Assert.IsFalse(invalid.IsOutcomeUnknown);
                StringAssert.Contains("does not match pattern", invalid.ServerMessage);
                logger.Flush(TimeSpan.FromSeconds(2));
            }
            var text = File.ReadAllText(_path);
            StringAssert.Contains("connection reset", text);
            StringAssert.Contains("does not match pattern", text);
        }

        [Test]
        public void ThrottledRequestsAreRetried()
        {
            var calls = 0;
            var transport = new FakeTransport(_ => ++calls == 1 ? (HttpStatusCode.TooManyRequests, "") : (HttpStatusCode.OK, "{\"positions\":[]}"));
            using var client = new TradeZeroRestClient("http://example.test", KeyId, Secret, TradeZeroApiLogger.Disabled, transport: transport);
            Assert.IsEmpty(client.GetPositions("TZP1"));
            Assert.AreEqual(2, calls);
        }

        [Test]
        public void BareNotFoundBlamesTheKeysOnlyWithoutARecentSuccess()
        {
            const string hint = "invalid API keys";
            var calls = 0;
            // 404, then 200, then 404: the keys worked in between
            var transport = new FakeTransport(_ => ++calls == 2 ? (HttpStatusCode.OK, "{\"positions\":[]}") : (HttpStatusCode.NotFound, "Not found\n"));
            using var client = new TradeZeroRestClient("http://example.test", KeyId, Secret, TradeZeroApiLogger.Disabled, transport: transport);

            StringAssert.Contains(hint, Assert.Throws<TradeZeroApiException>(() => client.GetPositions("TZP1")).Message);
            Assert.IsEmpty(client.GetPositions("TZP1"));
            var afterSuccess = Assert.Throws<TradeZeroApiException>(() => client.GetPositions("TZP1"));
            StringAssert.Contains("HTTP 404", afterSuccess.Message);
            StringAssert.DoesNotContain(hint, afterSuccess.Message);
        }

        [Test]
        public void RotatesLargeFiles()
        {
            using (var logger = new TradeZeroApiLogger(_path, new[] { KeyId }, maxBytes: 2_000))
            {
                for (var i = 0; i < 50; i++)
                {
                    logger.LogEvent("state", $"line {i} " + new string('x', 100));
                }
                logger.Flush(TimeSpan.FromSeconds(2));
            }
            Assert.Greater(Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*").Length, 1);
        }

        private class FakeTransport : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, (HttpStatusCode, string)> _answer;

            public FakeTransport(Func<HttpRequestMessage, (HttpStatusCode, string)> answer)
            {
                _answer = answer;
            }

            protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var (status, body) = _answer(request);
                return new HttpResponseMessage(status) { Content = new StringContent(body ?? string.Empty), RequestMessage = request };
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(Send(request, cancellationToken));
            }
        }
    }
}
