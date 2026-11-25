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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Logging;

namespace QuantConnect.Brokerages.TradeZero.Logging
{
    /// <summary>
    /// Writes every exchange with the TradeZero API, plus the brokerage's own decisions, to a JSON-lines
    /// file that can be shared for troubleshooting.
    /// </summary>
    /// <remarks>
    /// Secrets never reach the file:
    /// - HTTP headers (where the API key and secret travel) are never logged;
    /// - every line is scrubbed for the literal key and secret values before it is written.
    /// Repeated identical GET responses (polling) are collapsed: the next different response carries
    /// "unchangedBefore": N instead of N identical lines.
    /// Lines are written by a background thread, so logging never blocks order handling.
    /// </remarks>
    public sealed class TradeZeroApiLogger : IDisposable
    {
        private const int MaxBodyChars = 64 * 1024;
        private const string Mask = "***";

        private readonly string _path;
        private readonly long _maxBytes;
        private readonly bool _logBodies;
        private readonly string[] _secrets;
        private readonly BlockingCollection<string> _queue;
        private readonly Thread _writerThread;
        private readonly object _dedupLock = new();
        private readonly Dictionary<string, (int Hash, int Count)> _lastGetResponse = new();
        private int _disposed;

        /// <summary>
        /// A logger that writes nothing
        /// </summary>
        public static TradeZeroApiLogger Disabled { get; } = new TradeZeroApiLogger();

        /// <summary>
        /// True if this logger writes to a file
        /// </summary>
        public bool Enabled => _queue != null;

        /// <summary>
        /// The file being written, null when disabled
        /// </summary>
        public string FilePath => _path;

        private TradeZeroApiLogger()
        {
            _secrets = Array.Empty<string>();
        }

        /// <summary>
        /// Creates a logger writing to <paramref name="path"/>
        /// </summary>
        /// <param name="path">The JSON-lines file. Its directory is created if needed.</param>
        /// <param name="secrets">Values that must never appear in the file (API key id and secret)</param>
        /// <param name="maxBytes">When the file grows past this size it is renamed with a timestamp suffix and a new one is started</param>
        /// <param name="logBodies">False to log only method, path, status and timing</param>
        public TradeZeroApiLogger(string path, IEnumerable<string> secrets, long maxBytes = 50L * 1024 * 1024, bool logBodies = true)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A log file path is required", nameof(path));
            }
            _path = Path.GetFullPath(path);
            _maxBytes = maxBytes > 0 ? maxBytes : long.MaxValue;
            _logBodies = logBodies;
            // longest first so that a secret containing another one is fully masked
            _secrets = (secrets ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrEmpty(s) && s.Length >= 4)
                .Distinct()
                .OrderByDescending(s => s.Length)
                .ToArray();

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _queue = new BlockingCollection<string>(new ConcurrentQueue<string>());
            _writerThread = new Thread(WriterLoop) { IsBackground = true, Name = "TradeZeroApiLogger" };
            _writerThread.Start();
        }

        /// <summary>
        /// Logs one HTTP exchange
        /// </summary>
        public void LogHttp(string method, string pathAndQuery, string requestBody, int? status, string responseBody, long elapsedMs, string error = null)
        {
            if (!Enabled)
            {
                return;
            }

            int? unchangedBefore = null;
            if (method == "GET" && status == 200 && error == null)
            {
                var hash = (responseBody ?? string.Empty).GetHashCode();
                lock (_dedupLock)
                {
                    if (_lastGetResponse.TryGetValue(pathAndQuery, out var last) && last.Hash == hash)
                    {
                        _lastGetResponse[pathAndQuery] = (hash, last.Count + 1);
                        return;
                    }
                    if (last.Count > 0)
                    {
                        unchangedBefore = last.Count;
                    }
                    _lastGetResponse[pathAndQuery] = (hash, 0);
                }
            }

            var entry = NewEntry("http");
            entry["method"] = method;
            entry["path"] = pathAndQuery;
            if (status.HasValue)
            {
                entry["status"] = status.Value;
            }
            entry["ms"] = elapsedMs;
            if (unchangedBefore.HasValue)
            {
                entry["unchangedBefore"] = unchangedBefore.Value;
            }
            if (_logBodies)
            {
                if (!string.IsNullOrEmpty(requestBody))
                {
                    entry["request"] = Body(requestBody);
                }
                if (!string.IsNullOrEmpty(responseBody))
                {
                    entry["response"] = Body(responseBody);
                }
            }
            if (error != null)
            {
                entry["error"] = error;
            }
            Enqueue(entry);
        }

        /// <summary>
        /// Logs a brokerage-side event, e.g. how a Lean order was translated, or a connection state change
        /// </summary>
        /// <param name="kind">"lean" for order handling, "state" for connection/session events</param>
        /// <param name="message">Short description</param>
        /// <param name="data">Optional object serialized as the "data" field</param>
        public void LogEvent(string kind, string message, object data = null)
        {
            if (!Enabled)
            {
                return;
            }
            var entry = NewEntry(kind);
            entry["msg"] = message;
            if (data != null)
            {
                try
                {
                    entry["data"] = JToken.FromObject(data, JsonSerializer.CreateDefault(new JsonSerializerSettings
                    {
                        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                        NullValueHandling = NullValueHandling.Ignore
                    }));
                }
                catch (Exception e)
                {
                    entry["data"] = $"<unserializable {data.GetType().Name}: {e.Message}>";
                }
            }
            Enqueue(entry);
        }

        /// <summary>
        /// Replaces every secret value in <paramref name="text"/> with ***
        /// </summary>
        public string Redact(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }
            foreach (var secret in _secrets)
            {
                if (text.Contains(secret, StringComparison.Ordinal))
                {
                    text = text.Replace(secret, Mask, StringComparison.Ordinal);
                }
            }
            return text;
        }

        /// <summary>
        /// Blocks until queued lines are written (used by tests and on shutdown)
        /// </summary>
        public void Flush(TimeSpan timeout)
        {
            if (!Enabled)
            {
                return;
            }
            var deadline = DateTime.UtcNow + timeout;
            while (_queue.Count > 0 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }
            // let the writer finish the line it took
            Thread.Sleep(20);
        }

        public void Dispose()
        {
            if (!Enabled || Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }
            _queue.CompleteAdding();
            _writerThread.Join(TimeSpan.FromSeconds(5));
            _queue.Dispose();
        }

        private static JObject NewEntry(string kind)
        {
            return new JObject
            {
                ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                ["kind"] = kind
            };
        }

        private static JToken Body(string body)
        {
            if (body.Length > MaxBodyChars)
            {
                return body.Substring(0, MaxBodyChars) + $"...<truncated {body.Length - MaxBodyChars} chars>";
            }
            var trimmed = body.TrimStart();
            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            {
                try
                {
                    return JToken.Parse(body);
                }
                catch (JsonException)
                {
                    // not JSON after all, keep the raw text
                }
            }
            return body;
        }

        private void Enqueue(JObject entry)
        {
            if (_disposed == 1)
            {
                return;
            }
            var line = Redact(entry.ToString(Formatting.None));
            try
            {
                _queue.Add(line);
            }
            catch (InvalidOperationException)
            {
                // disposed while logging
            }
        }

        private void WriterLoop()
        {
            FileStream stream = null;
            try
            {
                foreach (var line in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        stream ??= Open();
                        var bytes = Encoding.UTF8.GetBytes(line + "\n");
                        stream.Write(bytes, 0, bytes.Length);
                        if (_queue.Count == 0)
                        {
                            stream.Flush();
                        }
                        if (stream.Length > _maxBytes)
                        {
                            stream.Dispose();
                            stream = null;
                            var rotated = $"{_path}.{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                            File.Move(_path, rotated, overwrite: true);
                        }
                    }
                    catch (Exception e)
                    {
                        Log.Error($"TradeZeroApiLogger: failed writing {_path}: {e.Message}");
                        stream?.Dispose();
                        stream = null;
                    }
                }
            }
            finally
            {
                stream?.Dispose();
            }
        }

        private FileStream Open()
        {
            return new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        }
    }
}
