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
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.TradeZero
{
    /// <summary>
    /// From TradeZero's tickers to Lean's symbols
    /// </summary>
    /// <remarks>
    /// A Lean equity's identifier is its first ticker and listing date, which Lean reads from the map files.
    /// A symbol built from today's ticker (<see cref="TradeZeroSymbolMapper.GetLeanSymbol(string)"/>) is the
    /// algorithm's only when Lean finds the same map file: ITP, first listed as ONP in 2009, is
    /// "ONP UIJ2Z3ZD225H" in the algorithm, but plain "ITP" without its map file, a separate security that
    /// holds nothing.
    /// </remarks>
    public partial class TradeZeroBrokerage
    {
        private readonly ConcurrentDictionary<string, bool> _unmappedTickersReported = new();

        /// <summary>
        /// The Lean symbol of a TradeZero ticker
        /// </summary>
        /// <remarks>
        /// A security the algorithm already has under this ticker (its current one) wins, whatever the map files
        /// say. Otherwise (at startup, before universe selection has added any) the symbol is built from the
        /// ticker and the map files, and a ticker without a map file is reported once: if it was renamed, its
        /// position or orders attach to another security than the algorithm's.
        /// </remarks>
        internal Symbol ResolveLeanSymbol(string ticker)
        {
            var symbol = MatchKnownSymbol(_symbolMapper.GetLeanSymbol(ticker), KnownLeanSymbols());
            if (IsUnmapped(symbol) && _unmappedTickersReported.TryAdd(symbol.Value, true))
            {
                Warn("UnmappedSymbol",
                    $"No map file for {symbol.Value}: TradeZero's {symbol.Value} becomes Lean security {symbol.ID}. If it was renamed, " +
                    "Lean knows it under another identifier and this position or order attaches to a separate security. " +
                    "Use LocalZipMapFileProvider (map-file-provider) with recent map files.");
            }
            return symbol;
        }

        /// <summary>
        /// The symbols of the algorithm's securities (none outside an algorithm, e.g. in tests)
        /// </summary>
        private IEnumerable<Symbol> KnownLeanSymbols()
        {
            // a cached snapshot, safe to read from the polling thread
            return _securityProvider is SecurityPortfolioManager portfolio ? portfolio.Keys : Enumerable.Empty<Symbol>();
        }

        /// <summary>
        /// Lean's equity holdings by current ticker, from Lean's own symbols; null outside an algorithm
        /// </summary>
        private Dictionary<string, decimal> LeanHoldingsByTicker()
        {
            if (_securityProvider is not SecurityPortfolioManager portfolio)
            {
                return null;
            }
            return QuantitiesByTicker(portfolio.Values.Where(h => h.Invested).Select(h => (h.Symbol, h.Quantity)));
        }

        /// <summary>
        /// The known equity symbol with the created one's current ticker: the created one itself if known,
        /// else the only other one; the created one when none or several match
        /// </summary>
        internal static Symbol MatchKnownSymbol(Symbol created, IEnumerable<Symbol> known)
        {
            Symbol match = null;
            var matches = 0;
            foreach (var symbol in known)
            {
                if (symbol.SecurityType != SecurityType.Equity || symbol.ID.Market != created.ID.Market ||
                    !string.Equals(symbol.Value, created.Value, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (symbol == created)
                {
                    return symbol;
                }
                match = symbol;
                matches++;
            }
            return matches == 1 ? match : created;
        }

        /// <summary>
        /// Whether Lean built this equity symbol without a map file (no first listing date)
        /// </summary>
        internal static bool IsUnmapped(Symbol symbol)
        {
            return symbol.SecurityType == SecurityType.Equity && symbol.ID.Date == SecurityIdentifier.DefaultDate;
        }

        /// <summary>
        /// Signed equity quantities summed by current ticker (upper case)
        /// </summary>
        internal static Dictionary<string, decimal> QuantitiesByTicker(IEnumerable<(Symbol Symbol, decimal Quantity)> holdings)
        {
            var result = new Dictionary<string, decimal>();
            foreach (var (symbol, quantity) in holdings)
            {
                if (symbol.SecurityType != SecurityType.Equity || quantity == 0)
                {
                    continue;
                }
                var ticker = symbol.Value.ToUpperInvariant();
                result[ticker] = result.GetValueOrDefault(ticker) + quantity;
            }
            return result;
        }
    }
}
