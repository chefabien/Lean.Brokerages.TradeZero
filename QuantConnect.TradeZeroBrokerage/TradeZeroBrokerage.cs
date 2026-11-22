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
using QuantConnect.Brokerages.TradeZero.Api;
using QuantConnect.Brokerages.TradeZero.Logging;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.TradeZero
{
    /// <summary>
    /// TradeZero brokerage for Lean, over the REST API only (the WebSocket API is beta).
    /// Order status, fills and account figures are polled.
    /// </summary>
    /// <remarks>
    /// Scope: US stocks, long and short (hard-to-borrow shorts through locates, see <see cref="TradeZeroOrderProperties.LocateHandler"/>
    /// and <see cref="TradeZeroLocates"/>), whole shares.
    /// Safety: refuses to connect when the account type does not match <see cref="TradeZeroSettings.Environment"/>
    /// (paper by default).
    /// </remarks>
    [BrokerageFactory(typeof(TradeZeroBrokerageFactory))]
    public partial class TradeZeroBrokerage : Brokerage
    {
        private readonly TradeZeroSettings _settings;
        private readonly IOrderProvider _orderProvider;
        private readonly ISecurityProvider _securityProvider;
        private readonly TradeZeroSymbolMapper _symbolMapper = new();
        private readonly TradeZeroFeeModel _feeModel;
        private readonly TradeZeroApiLogger _apiLogger;
        private readonly TradeZeroRestClient _client;
        private readonly ConcurrentDictionary<string, (DateTime Day, bool EasyToBorrow)> _easyToBorrowCache = new();
        private readonly Dictionary<string, int> _positionMismatches = new();
        private readonly object _connectionLock = new();

        private string _accountId;
        private string _route;
        private TradeZeroAccount _account = TradeZeroAccount.Empty();

        private Thread _pollThread;
        private CancellationTokenSource _pollCancellation;
        private readonly AutoResetEvent _pollWakeUp = new(false);
        private volatile bool _connected;
        private volatile bool _disconnectReported;
        private int _consecutivePollFailures;
        private DateTime _nextOrdersPollUtc;
        private DateTime _nextPnlPollUtc;
        private DateTime _nextReconcileUtc;
        private int _pnlPollCount;

        /// <summary>
        /// True while connected and polling succeeds
        /// </summary>
        public override bool IsConnected => _connected && !_disconnectReported;

        /// <summary>
        /// The TradeZero account this brokerage trades
        /// </summary>
        public string AccountId => _accountId;

        /// <summary>
        /// Latest account snapshot (broker-side margin and P&amp;L figures)
        /// </summary>
        public TradeZeroAccount Account => _account;

        /// <summary>
        /// The API exchange log file, null when disabled
        /// </summary>
        public string ApiLogFile => _apiLogger.FilePath;

        public override string AccountBaseCurrency => Currencies.USD;

        /// <summary>
        /// Creates the brokerage. Nothing is sent to TradeZero before <see cref="Connect"/>.
        /// </summary>
        /// <param name="settings">Connection, logging and polling settings</param>
        /// <param name="orderProvider">Lean's orders (to map TradeZero orders back to Lean orders)</param>
        /// <param name="securityProvider">Lean's holdings (to choose Open/Close on each order)</param>
        /// <param name="httpTransport">Optional HTTP transport, for tests</param>
        public TradeZeroBrokerage(TradeZeroSettings settings, IOrderProvider orderProvider, ISecurityProvider securityProvider,
            System.Net.Http.HttpMessageHandler httpTransport = null)
            : base("TradeZero")
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _orderProvider = orderProvider ?? throw new ArgumentNullException(nameof(orderProvider));
            _securityProvider = securityProvider ?? throw new ArgumentNullException(nameof(securityProvider));
            if (string.IsNullOrWhiteSpace(settings.ApiKeyId) || string.IsNullOrWhiteSpace(settings.ApiSecretKey))
            {
                throw new ArgumentException($"TradeZero API keys are missing: set {TradeZeroSettings.ApiKeyIdKey} and {TradeZeroSettings.ApiSecretKeyKey} " +
                    $"(or the {TradeZeroSettings.ApiKeyIdEnvironmentVariable} / {TradeZeroSettings.ApiSecretKeyEnvironmentVariable} environment variables).");
            }

            _feeModel = new TradeZeroFeeModel(settings.FeePerOrder, settings.FeePerShare);
            _apiLogger = string.IsNullOrWhiteSpace(settings.ApiLogFile)
                ? TradeZeroApiLogger.Disabled
                : new TradeZeroApiLogger(settings.ApiLogFile, new[] { settings.ApiKeyId, settings.ApiSecretKey },
                    settings.ApiLogMaxMb * 1024L * 1024L, settings.ApiLogBodies);
            _client = new TradeZeroRestClient(settings.ApiUrl, settings.ApiKeyId, settings.ApiSecretKey, _apiLogger, transport: httpTransport);
            _runTag = DateTime.UtcNow.ToString("MMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            InitializeLocates();

            if (_apiLogger.Enabled)
            {
                Log.Trace($"TradeZeroBrokerage: logging API exchanges to {_apiLogger.FilePath}");
            }
        }

        #region Connection

        /// <summary>
        /// Selects the account, checks it matches the configured environment (paper/live), then starts polling
        /// </summary>
        public override void Connect()
        {
            lock (_connectionLock)
            {
                if (_connected)
                {
                    return;
                }
                _apiLogger.LogEvent("state", "connect", new { baseUrl = _client.BaseUrl.ToString(), environment = _settings.Environment.ToString(), configuredAccount = _settings.AccountId });

                var accounts = _client.GetAccounts();
                var selected = SelectAccount(accounts, _settings.AccountId);
                var detail = _client.GetAccount(selected.Account);
                var accountType = detail.AccountType ?? selected.AccountType;
                EnsureEnvironment(_settings.Environment, selected.Account, accountType);

                _accountId = selected.Account;
                if (detail.TradingSuspended)
                {
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "TradingSuspended", $"TradeZero reports trading suspended on account {_accountId}."));
                }
                _route = ResolveRoute(accountType);

                var now = DateTime.UtcNow;
                var pnl = _client.GetPnl(_accountId);
                _account = TradeZeroAccount.Empty().WithAccount(detail, now).WithPnl(pnl, now);
                if (pnl.AllowedLeverage > 0 && pnl.AllowedLeverage < _settings.IntradayLeverage)
                {
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "LeverageMismatch",
                        $"TradeZero allows {pnl.AllowedLeverage}x leverage on account {selected.Account} but Lean's TradeZero model uses up to " +
                        $"{_settings.IntradayLeverage}x: Lean may send orders TradeZero rejects for buying power (R100). Set " +
                        $"{TradeZeroSettings.IntradayLeverageKey} and {TradeZeroSettings.OvernightLeverageKey} to {pnl.AllowedLeverage}."));
                }
                TradeZeroAccount.Publish(_account);
                TradeZeroLocates.Register(this);

                _apiLogger.LogEvent("state", "connected", new { account = _accountId, accountType, route = _route });
                Log.Trace($"TradeZeroBrokerage.Connect(): connected to {_client.BaseUrl} account {_accountId} ({accountType}), route: {_route ?? "<server default>"}");

                _connected = true;
                _disconnectReported = false;
                _consecutivePollFailures = 0;
                _nextOrdersPollUtc = _nextPnlPollUtc = now;
                _nextReconcileUtc = now + _settings.ReconcileInterval;
                _pollCancellation = new CancellationTokenSource();
                _pollThread = new Thread(() => PollLoop(_pollCancellation.Token)) { IsBackground = true, Name = "TradeZeroPolling" };
                _pollThread.Start();
            }
        }

        public override void Disconnect()
        {
            lock (_connectionLock)
            {
                if (!_connected)
                {
                    return;
                }
                _connected = false;
                _pollCancellation?.Cancel();
                _pollWakeUp.Set();
                if (_pollThread != null && _pollThread != Thread.CurrentThread)
                {
                    _pollThread.Join(TimeSpan.FromSeconds(10));
                }
                _pollCancellation?.Dispose();
                _pollCancellation = null;
                _pollThread = null;
                TradeZeroLocates.Unregister(this);
                TradeZeroAccount.Clear(_accountId);
                _apiLogger.LogEvent("state", "disconnected", new { account = _accountId });
                Log.Trace("TradeZeroBrokerage.Disconnect(): stopped polling");
            }
        }

        public override void Dispose()
        {
            Disconnect();
            _client.Dispose();
            _apiLogger.Dispose();
            _pollWakeUp.Dispose();
            base.Dispose();
        }

        /// <summary>
        /// Picks the configured account, or the only one available
        /// </summary>
        internal static TzAccount SelectAccount(IReadOnlyCollection<TzAccount> accounts, string configuredAccountId)
        {
            if (accounts == null || accounts.Count == 0)
            {
                throw new InvalidOperationException("TradeZero returned no account for these API keys.");
            }
            if (!string.IsNullOrWhiteSpace(configuredAccountId))
            {
                return accounts.FirstOrDefault(a => string.Equals(a.Account, configuredAccountId.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"TradeZero account '{configuredAccountId}' is not available with these API keys. " +
                        $"Available: {string.Join(", ", accounts.Select(a => a.Account))}");
            }
            if (accounts.Count > 1)
            {
                throw new InvalidOperationException($"Several TradeZero accounts are available ({string.Join(", ", accounts.Select(a => a.Account))}): " +
                    $"set {TradeZeroSettings.AccountIdKey}.");
            }
            return accounts.First();
        }

        /// <summary>
        /// The paper/live guard: the account type must match the configured environment
        /// </summary>
        internal static void EnsureEnvironment(TradeZeroEnvironment environment, string accountId, string accountType)
        {
            var isPaper = string.Equals(accountType, TzValues.Paper, StringComparison.OrdinalIgnoreCase);
            if (environment == TradeZeroEnvironment.Paper && !isPaper)
            {
                throw new InvalidOperationException($"Refusing to connect: {TradeZeroSettings.EnvironmentKey} is 'paper' but TradeZero account {accountId} " +
                    $"is of type '{accountType ?? "<unknown>"}', not 'Paper'. Check the API keys; set {TradeZeroSettings.EnvironmentKey}=live only to trade a live account.");
            }
            if (environment == TradeZeroEnvironment.Live && isPaper)
            {
                throw new InvalidOperationException($"Refusing to connect: {TradeZeroSettings.EnvironmentKey} is 'live' but TradeZero account {accountId} is a Paper account.");
            }
        }

        private string ResolveRoute(string accountType)
        {
            if (!string.IsNullOrWhiteSpace(_settings.Route))
            {
                return _settings.Route.Trim();
            }
            if (string.Equals(accountType, TzValues.Paper, StringComparison.OrdinalIgnoreCase))
            {
                // paper assigns its own route (PAPER)
                return null;
            }
            try
            {
                var stockRoutes = _client.GetRoutes(_accountId)
                    .Where(r => r.SecurityTypes.Any(t => string.Equals(t, TzValues.Stock, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                var route = stockRoutes.FirstOrDefault(r => r.RouteName == "SMART") ?? stockRoutes.FirstOrDefault();
                return route?.RouteName;
            }
            catch (Exception e)
            {
                Log.Error($"TradeZeroBrokerage: could not read routes ({e.Message}); orders are sent without a route. Set {TradeZeroSettings.RouteKey}.");
                return null;
            }
        }

        #endregion

        #region Account

        /// <summary>
        /// Cash as Lean understands it: account value minus the market value of the positions
        /// (short sale proceeds count as cash, unlike TradeZero's availableCash).
        /// </summary>
        public override List<CashAmount> GetCashBalance()
        {
            var pnl = _client.GetPnl(_accountId);
            var positions = _client.GetPositions(_accountId);
            UpdatePnlSnapshot(pnl);
            var cash = ComputeLeanCash(pnl, positions);
            _apiLogger.LogEvent("lean", "cash balance", new { accountValue = pnl.AccountValue, cash });
            return new List<CashAmount> { new(cash, Currencies.USD) };
        }

        public override List<Holding> GetAccountHoldings()
        {
            var positions = _client.GetPositions(_accountId);
            var pnl = _client.GetPnl(_accountId);
            UpdatePnlSnapshot(pnl);

            var holdings = new List<Holding>();
            foreach (var position in positions)
            {
                var quantity = position.SignedShares;
                if (quantity == 0)
                {
                    continue;
                }
                if (position.SecurityType != null && !string.Equals(position.SecurityType, TzValues.Stock, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Trace($"TradeZeroBrokerage.GetAccountHoldings(): skipping unsupported {position.SecurityType} position {position.Symbol}");
                    continue;
                }
                var price = MarkPrice(position, pnl);
                var row = FindPnlRow(position, pnl);
                holdings.Add(new Holding
                {
                    Symbol = _symbolMapper.GetLeanSymbol(position.Symbol),
                    Quantity = quantity,
                    AveragePrice = position.PriceAvg,
                    MarketPrice = price,
                    MarketValue = price * quantity,
                    UnrealizedPnL = row?.UnrealizedPnL ?? (price - position.PriceAvg) * quantity,
                    CurrencySymbol = "$"
                });
            }
            return holdings;
        }

        /// <summary>
        /// Lean cash = account value - Σ signed position market value
        /// </summary>
        internal static decimal ComputeLeanCash(TzPnl pnl, IEnumerable<TzPosition> positions)
        {
            var marketValue = positions.Where(p => p.SignedShares != 0).Sum(p => p.SignedShares * MarkPrice(p, pnl));
            return pnl.AccountValue - marketValue;
        }

        /// <summary>
        /// Current price of a position, from the P&amp;L exposure (falls back to the average price)
        /// </summary>
        internal static decimal MarkPrice(TzPosition position, TzPnl pnl)
        {
            var row = FindPnlRow(position, pnl);
            var shares = Math.Abs(position.SignedShares);
            if (row != null && row.Exposure != 0 && shares != 0)
            {
                return Math.Abs(row.Exposure) / shares;
            }
            return position.PriceAvg;
        }

        private static TzPnlRow FindPnlRow(TzPosition position, TzPnl pnl)
        {
            var rows = pnl?.Rows;
            if (rows == null || rows.Count == 0)
            {
                return null;
            }
            return rows.FirstOrDefault(r => r.PositionId != null && r.PositionId == position.PositionId)
                ?? rows.FirstOrDefault(r => r.Symbol == position.Symbol && r.Exposure != 0);
        }

        private void UpdatePnlSnapshot(TzPnl pnl)
        {
            _account = _account.WithPnl(pnl, DateTime.UtcNow);
            if (_connected)
            {
                TradeZeroAccount.Publish(_account);
            }
        }

        private bool? ResolveEasyToBorrow(string ticker)
        {
            var today = DateTime.UtcNow.ConvertFromUtc(TimeZones.NewYork).Date;
            if (_easyToBorrowCache.TryGetValue(ticker, out var cached) && cached.Day == today)
            {
                return cached.EasyToBorrow;
            }
            if (_accountId == null)
            {
                return null;
            }
            try
            {
                var easyToBorrow = _client.IsEasyToBorrow(_accountId, ticker);
                _easyToBorrowCache[ticker] = (today, easyToBorrow);
                return easyToBorrow;
            }
            catch (Exception e)
            {
                Log.Error($"TradeZeroBrokerage: easy-to-borrow check failed for {ticker}: {e.Message}");
                return null;
            }
        }

        #endregion

        #region Polling

        private void PollLoop(CancellationToken token)
        {
            Log.Trace("TradeZeroBrokerage: polling started");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    PollOnce();
                }
                catch (Exception e)
                {
                    // PollOnce handles API errors; this is a bug guard so the loop never dies
                    Log.Error(e, "TradeZeroBrokerage.PollLoop()");
                }

                var next = new[] { _nextOrdersPollUtc, _nextPnlPollUtc, _nextReconcileUtc }.Min();
                var wait = next - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    WaitHandle.WaitAny(new[] { token.WaitHandle, _pollWakeUp }, wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait);
                }
            }
            Log.Trace("TradeZeroBrokerage: polling stopped");
        }

        /// <summary>
        /// Runs whatever poll is due. Public for tests that drive the clock (tz-mock) and need an immediate refresh.
        /// </summary>
        public void PollOnce(bool force = false)
        {
            var now = DateTime.UtcNow;
            if (force || now >= _nextOrdersPollUtc)
            {
                if (Try(PollOrders))
                {
                    _nextOrdersPollUtc = DateTime.UtcNow + (HasWorkingOrders() ? _settings.ActivePollInterval : _settings.IdlePollInterval);
                }
                else
                {
                    _nextOrdersPollUtc = DateTime.UtcNow + _settings.IdlePollInterval;
                }
            }
            if (force || now >= _nextPnlPollUtc)
            {
                Try(PollAccount);
                _nextPnlPollUtc = DateTime.UtcNow + _settings.AccountPollInterval;
            }
            if (now >= _nextReconcileUtc)
            {
                Try(ReconcilePositions);
                _nextReconcileUtc = DateTime.UtcNow + _settings.ReconcileInterval;
            }
        }

        /// <summary>
        /// Asks the polling loop to check orders soon (after placing/updating an order)
        /// </summary>
        private void PollOrdersSoon(TimeSpan delay)
        {
            var at = DateTime.UtcNow + delay;
            if (at < _nextOrdersPollUtc)
            {
                _nextOrdersPollUtc = at;
            }
            _pollWakeUp.Set();
        }

        private bool Try(Action poll)
        {
            if (!_connected)
            {
                return false;
            }
            try
            {
                poll();
                if (_consecutivePollFailures > 0 || _disconnectReported)
                {
                    _consecutivePollFailures = 0;
                    if (_disconnectReported)
                    {
                        _disconnectReported = false;
                        _apiLogger.LogEvent("state", "reconnected");
                        OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Reconnect, "Reconnected", "TradeZero API reachable again."));
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                _consecutivePollFailures++;
                Log.Error($"TradeZeroBrokerage: polling failed ({_consecutivePollFailures} in a row): {e.Message}");
                if (_consecutivePollFailures >= 3 && !_disconnectReported)
                {
                    _disconnectReported = true;
                    _apiLogger.LogEvent("state", "disconnected (polling failing)", new { error = e.Message });
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Disconnect, "PollingFailed",
                        $"TradeZero API unreachable or failing: {e.Message}"));
                }
                return false;
            }
        }

        private void PollAccount()
        {
            var now = DateTime.UtcNow;
            var pnl = _client.GetPnl(_accountId);
            var snapshot = _account.WithPnl(pnl, now);
            // the /account figures (buying power, margin requirement) are refreshed every 6th poll
            if (_pnlPollCount++ % 6 == 0)
            {
                snapshot = snapshot.WithAccount(_client.GetAccount(_accountId), now);
            }
            _account = snapshot;
            TradeZeroAccount.Publish(snapshot);
        }

        /// <summary>
        /// Warns when TradeZero's positions differ from Lean's holdings twice in a row, for symbols
        /// without working orders (fills in flight would make them differ briefly)
        /// </summary>
        private void ReconcilePositions()
        {
            var positions = _client.GetPositions(_accountId)
                .Where(p => p.SignedShares != 0)
                .GroupBy(p => p.Symbol)
                .ToDictionary(g => g.Key, g => g.Sum(p => p.SignedShares));

            HashSet<string> busy;
            lock (_orderLock)
            {
                busy = _ordersByLeanId.Values.Where(t => !t.Done).Select(t => t.Ticker).ToHashSet();
            }

            var tickers = positions.Keys.ToHashSet();
            foreach (var security in SecuritiesWithHoldings())
            {
                tickers.Add(security);
            }
            foreach (var ticker in tickers)
            {
                if (busy.Contains(ticker))
                {
                    _positionMismatches.Remove(ticker);
                    continue;
                }
                positions.TryGetValue(ticker, out var broker);
                var lean = _securityProvider.GetHoldingsQuantity(_symbolMapper.GetLeanSymbol(ticker));
                if (broker == lean)
                {
                    _positionMismatches.Remove(ticker);
                    continue;
                }
                var count = _positionMismatches.TryGetValue(ticker, out var c) ? c + 1 : 1;
                _positionMismatches[ticker] = count;
                if (count == 2)
                {
                    var message = $"Position mismatch on {ticker}: TradeZero {broker}, Lean {lean}.";
                    _apiLogger.LogEvent("lean", "position mismatch", new { ticker, broker, lean });
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "PositionMismatch", message));
                }
            }
        }

        private IEnumerable<string> SecuritiesWithHoldings()
        {
            // the security provider has no enumeration; Lean's portfolio does
            if (_securityProvider is SecurityPortfolioManager portfolio)
            {
                return portfolio.Values
                    .Where(h => h.Invested && h.Symbol.SecurityType == SecurityType.Equity)
                    .Select(h => h.Symbol.Value)
                    .ToList();
            }
            return Enumerable.Empty<string>();
        }

        #endregion
    }
}
