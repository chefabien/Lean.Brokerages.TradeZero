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
using System.Text.RegularExpressions;

namespace QuantConnect.Brokerages.TradeZero
{
    /// <summary>
    /// Maps Lean US equity symbols to TradeZero tickers. Both use the same ticker spelling
    /// (class shares with a dot, e.g. BRK.B), so this only validates and normalizes.
    /// </summary>
    public class TradeZeroSymbolMapper : ISymbolMapper
    {
        // the server's own validation pattern for /symbol
        private static readonly Regex ValidTicker = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

        public string GetBrokerageSymbol(Symbol symbol)
        {
            if (symbol == null || string.IsNullOrWhiteSpace(symbol.Value))
            {
                throw new ArgumentException("Invalid symbol: null or empty");
            }
            if (symbol.SecurityType != SecurityType.Equity)
            {
                throw new ArgumentException($"TradeZero brokerage only supports US equities, not {symbol.SecurityType} ({symbol})");
            }
            if (symbol.ID.Market != Market.USA)
            {
                throw new ArgumentException($"TradeZero brokerage only supports market {Market.USA}, not {symbol.ID.Market} ({symbol})");
            }
            var ticker = symbol.Value.ToUpperInvariant();
            if (!ValidTicker.IsMatch(ticker))
            {
                throw new ArgumentException($"Ticker not accepted by TradeZero: {ticker}");
            }
            return ticker;
        }

        public Symbol GetLeanSymbol(string brokerageSymbol, SecurityType securityType, string market,
            DateTime expirationDate = default, decimal strike = 0, OptionRight optionRight = 0)
        {
            if (string.IsNullOrWhiteSpace(brokerageSymbol))
            {
                throw new ArgumentException("Invalid TradeZero symbol: null or empty");
            }
            if (securityType != SecurityType.Equity)
            {
                throw new ArgumentException($"TradeZero brokerage only supports US equities, not {securityType}");
            }
            return Symbol.Create(brokerageSymbol.Trim().ToUpperInvariant(), SecurityType.Equity, string.IsNullOrEmpty(market) ? Market.USA : market);
        }

        /// <summary>
        /// Lean symbol for a TradeZero stock ticker
        /// </summary>
        public Symbol GetLeanSymbol(string brokerageSymbol)
        {
            return GetLeanSymbol(brokerageSymbol, SecurityType.Equity, Market.USA);
        }
    }
}
