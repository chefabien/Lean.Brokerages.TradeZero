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
using Newtonsoft.Json;

namespace QuantConnect.Brokerages.TradeZero.Api
{
    // TradeZero REST payloads. Field names follow the paper API (see tz-mock/docs/api-notes.md).
    // Only the fields the brokerage uses are mapped; unknown fields are ignored.

    /// <summary>
    /// String values used by the TradeZero order API (all case-sensitive on the server)
    /// </summary>
    public static class TzValues
    {
        public const string Stock = "Stock";

        public const string Buy = "Buy";
        public const string Sell = "Sell";

        public const string Open = "Open";
        public const string Close = "Close";

        public const string Market = "Market";
        public const string Limit = "Limit";
        public const string Stop = "Stop";
        public const string StopLimit = "StopLimit";

        public const string Day = "Day";
        public const string GoodTillCancel = "GoodTillCancel";
        public const string AtTheOpening = "AtTheOpening";
        public const string ImmediateOrCancel = "ImmediateOrCancel";
        public const string FillOrKill = "FillOrKill";
        public const string GoodTillCrossing = "GoodTillCrossing";
        public const string DayPlus = "Day_Plus";
        public const string GtcPlus = "GTC_Plus";

        public const string Paper = "Paper";
    }

    /// <summary>
    /// TradeZero order statuses
    /// </summary>
    public static class TzOrderStatus
    {
        public const string PendingNew = "PendingNew";
        public const string New = "New";
        public const string Accepted = "Accepted";
        public const string PartiallyFilled = "PartiallyFilled";
        public const string PendingCancel = "PendingCancel";
        public const string PendingReplace = "PendingReplace";
        public const string Filled = "Filled";
        public const string Canceled = "Canceled";
        public const string Rejected = "Rejected";
        public const string Expired = "Expired";
        public const string DoneForDay = "DoneForDay";

        /// <summary>
        /// True if the status is final: the order will never change again
        /// </summary>
        public static bool IsTerminal(string status)
        {
            return status == Filled || status == Canceled || status == Rejected || status == Expired || status == DoneForDay;
        }
    }

    public class TzAccount
    {
        [JsonProperty("account")] public string Account { get; set; }
        [JsonProperty("accountStatus")] public string AccountStatus { get; set; }
        [JsonProperty("accountType")] public string AccountType { get; set; }
        [JsonProperty("availableCash")] public decimal AvailableCash { get; set; }
        /// <summary>GET /accounts calls it buyingPower, GET /account/{id} calls it bp</summary>
        [JsonProperty("buyingPower")] public decimal? BuyingPower { get; set; }
        [JsonProperty("bp")] public decimal? Bp { get; set; }
        [JsonProperty("equity")] public decimal Equity { get; set; }
        [JsonProperty("leverage")] public decimal Leverage { get; set; }
        [JsonProperty("maintenanceDeficit")] public decimal MaintenanceDeficit { get; set; }
        [JsonProperty("marginDeficit")] public decimal MarginDeficit { get; set; }
        [JsonProperty("marginRequirement")] public decimal MarginRequirement { get; set; }
        [JsonProperty("marginType")] public string MarginType { get; set; }
        [JsonProperty("overnightBp")] public decimal OvernightBp { get; set; }
        [JsonProperty("sodEquity")] public decimal SodEquity { get; set; }
        [JsonProperty("totalCommissions")] public decimal TotalCommissions { get; set; }
        [JsonProperty("tradingSuspended")] public bool TradingSuspended { get; set; }
        [JsonProperty("fractional")] public bool Fractional { get; set; }

        [JsonIgnore] public decimal EffectiveBuyingPower => Bp ?? BuyingPower ?? 0m;
    }

    public class TzAccountsResponse
    {
        [JsonProperty("accounts")] public List<TzAccount> Accounts { get; set; } = new();
    }

    public class TzPnlRow
    {
        [JsonProperty("positionId")] public string PositionId { get; set; }
        [JsonProperty("symbol")] public string Symbol { get; set; }
        [JsonProperty("exposure")] public decimal Exposure { get; set; }
        [JsonProperty("unrealizedPnL")] public decimal UnrealizedPnL { get; set; }
        [JsonProperty("realizedPnl")] public decimal RealizedPnl { get; set; }
    }

    public class TzPnl
    {
        [JsonProperty("accountValue")] public decimal AccountValue { get; set; }
        [JsonProperty("allowedLeverage")] public decimal AllowedLeverage { get; set; }
        [JsonProperty("availableCash")] public decimal AvailableCash { get; set; }
        [JsonProperty("dayPnl")] public decimal DayPnl { get; set; }
        [JsonProperty("dayRealized")] public decimal DayRealized { get; set; }
        [JsonProperty("dayUnrealized")] public decimal DayUnrealized { get; set; }
        [JsonProperty("equityRatio")] public decimal EquityRatio { get; set; }
        [JsonProperty("exposure")] public decimal Exposure { get; set; }
        [JsonProperty("initialMargin")] public decimal InitialMargin { get; set; }
        [JsonProperty("intradayMargin")] public decimal IntradayMargin { get; set; }
        [JsonProperty("maintenanceMargin")] public decimal MaintenanceMargin { get; set; }
        [JsonProperty("marginExcess")] public decimal MarginExcess { get; set; }
        [JsonProperty("sharesTraded")] public decimal SharesTraded { get; set; }
        [JsonProperty("totalUnrealized")] public decimal TotalUnrealized { get; set; }
        [JsonProperty("usedLeverage")] public decimal UsedLeverage { get; set; }
        [JsonProperty("pnl")] public List<TzPnlRow> Rows { get; set; } = new();
    }

    public class TzPosition
    {
        [JsonProperty("positionId")] public string PositionId { get; set; }
        [JsonProperty("symbol")] public string Symbol { get; set; }
        [JsonProperty("securityType")] public string SecurityType { get; set; }
        /// <summary>Paper sends a positive number with <see cref="Side"/>; the stream docs use a signed number. Use <see cref="SignedShares"/>.</summary>
        [JsonProperty("shares")] public decimal Shares { get; set; }
        [JsonProperty("side")] public string Side { get; set; }
        [JsonProperty("priceAvg")] public decimal PriceAvg { get; set; }
        [JsonProperty("priceClose")] public decimal PriceClose { get; set; }
        [JsonProperty("dayOvernight")] public string DayOvernight { get; set; }

        /// <summary>
        /// Position quantity, negative for a short
        /// </summary>
        [JsonIgnore]
        public decimal SignedShares => string.Equals(Side, "Short", StringComparison.OrdinalIgnoreCase) ? -Math.Abs(Shares) : Math.Abs(Shares);
    }

    public class TzPositionsResponse
    {
        [JsonProperty("positions")] public List<TzPosition> Positions { get; set; } = new();
    }

    public class TzOrder
    {
        [JsonProperty("accountId")] public string AccountId { get; set; }
        [JsonProperty("clientOrderId")] public string ClientOrderId { get; set; }
        [JsonProperty("symbol")] public string Symbol { get; set; }
        [JsonProperty("securityType")] public string SecurityType { get; set; }
        [JsonProperty("side")] public string Side { get; set; }
        [JsonProperty("openClose")] public string OpenClose { get; set; }
        [JsonProperty("orderType")] public string OrderType { get; set; }
        [JsonProperty("timeInForce")] public string TimeInForce { get; set; }
        [JsonProperty("orderStatus")] public string OrderStatus { get; set; }
        [JsonProperty("orderQuantity")] public decimal OrderQuantity { get; set; }
        [JsonProperty("executed")] public decimal Executed { get; set; }
        [JsonProperty("leavesQuantity")] public decimal LeavesQuantity { get; set; }
        [JsonProperty("canceledQuantity")] public decimal CanceledQuantity { get; set; }
        [JsonProperty("priceAvg")] public decimal PriceAvg { get; set; }
        [JsonProperty("lastPrice")] public decimal LastPrice { get; set; }
        [JsonProperty("lastQuantity")] public decimal LastQuantity { get; set; }
        [JsonProperty("limitPrice")] public decimal LimitPrice { get; set; }
        [JsonProperty("priceStop")] public decimal PriceStop { get; set; }
        [JsonProperty("route")] public string Route { get; set; }
        [JsonProperty("text")] public string Text { get; set; }
        [JsonProperty("startTime")] public DateTime? StartTime { get; set; }
        [JsonProperty("lastUpdated")] public DateTime? LastUpdated { get; set; }

        /// <summary>
        /// The side as sent: reads may enrich it to SellShort/BuyToCover
        /// </summary>
        [JsonIgnore]
        public bool IsBuy => Side != null && (Side == TzValues.Buy || Side.StartsWith("BuyTo", StringComparison.Ordinal));

        public override string ToString()
        {
            return $"{ClientOrderId} {Side}/{OpenClose} {OrderQuantity} {Symbol} {OrderType} {TimeInForce} {OrderStatus} exec={Executed}@{PriceAvg} text={Text}";
        }
    }

    public class TzOrdersResponse
    {
        [JsonProperty("orders")] public List<TzOrder> Orders { get; set; } = new();
    }

    /// <summary>
    /// Body of POST /v1/api/accounts/{accountId}/order
    /// </summary>
    public class TzOrderRequest
    {
        [JsonProperty("clientOrderId")] public string ClientOrderId { get; set; }
        [JsonProperty("symbol")] public string Symbol { get; set; }
        [JsonProperty("securityType")] public string SecurityType { get; set; } = TzValues.Stock;
        [JsonProperty("side")] public string Side { get; set; }
        [JsonProperty("openClose")] public string OpenClose { get; set; }
        [JsonProperty("orderType")] public string OrderType { get; set; }
        [JsonProperty("orderQuantity")] public long OrderQuantity { get; set; }
        [JsonProperty("timeInForce")] public string TimeInForce { get; set; }
        [JsonProperty("limitPrice", NullValueHandling = NullValueHandling.Ignore)] public decimal? LimitPrice { get; set; }
        [JsonProperty("stopPrice", NullValueHandling = NullValueHandling.Ignore)] public decimal? StopPrice { get; set; }
        [JsonProperty("route", NullValueHandling = NullValueHandling.Ignore)] public string Route { get; set; }

        public override string ToString()
        {
            return $"{ClientOrderId} {Side}/{OpenClose} {OrderQuantity} {Symbol} {OrderType} {TimeInForce} lmt={LimitPrice} stp={StopPrice} route={Route}";
        }
    }

    public class TzRoute
    {
        [JsonProperty("routeName")] public string RouteName { get; set; }
        [JsonProperty("securityTypes")] public List<string> SecurityTypes { get; set; } = new();
        [JsonProperty("orderTypes")] public List<string> OrderTypes { get; set; } = new();
        [JsonProperty("timesInForce")] public List<string> TimesInForce { get; set; } = new();
    }

    public class TzRoutesResponse
    {
        [JsonProperty("routes")] public List<TzRoute> Routes { get; set; } = new();
    }

    public class TzEasyToBorrowResponse
    {
        [JsonProperty("isEasyToBorrow")] public bool IsEasyToBorrow { get; set; }
    }

    /// <summary>
    /// <c>locateStatus</c> codes of the locate history (Short Locates guide)
    /// </summary>
    public static class TzLocateStatus
    {
        /// <summary>Sell-back queued</summary>
        public const int New = 48;
        /// <summary>Accepted locate, or settled sell-back</summary>
        public const int Filled = 50;
        public const int Canceled = 52;
        /// <summary>Pending, or (with a locateError) refused for this pool: "11 No shares available"</summary>
        public const int Pending = 54;
        public const int Rejected = 56;
        /// <summary>Offer to accept within 30 s</summary>
        public const int Offered = 65;
        public const int Expired = 67;
        public const int Quoting = 81;
    }

    /// <summary>
    /// <c>locateType</c> codes: 0 unknown (refused rows), 1 and 2 ordinary locate (two pools), 3 PreBorrow, 4 SingleUse
    /// </summary>
    public static class TzLocateType
    {
        /// <summary>The <c>/sell</c> string for a locate type</summary>
        public static string SellName(TradeZeroLocateType type) => type switch
        {
            TradeZeroLocateType.Locate => "Locate",
            TradeZeroLocateType.PreBorrow => "PreBorrow",
            TradeZeroLocateType.SingleUse => "SingleUse",
            _ => "Unknown"
        };

        /// <summary>Maps a <c>locateType</c> code; the <c>preBorrow</c> boolean is ignored, as the guide recommends</summary>
        public static TradeZeroLocateType FromCode(int code) => code switch
        {
            1 or 2 => TradeZeroLocateType.Locate,
            3 => TradeZeroLocateType.PreBorrow,
            4 => TradeZeroLocateType.SingleUse,
            _ => TradeZeroLocateType.Unknown
        };
    }

    /// <summary>
    /// A row of GET /accounts/{a}/locates/history: a quote (one row, or two for a Reg SHO symbol: id and id + ".SU"),
    /// or a sell-back (negative <see cref="LocateShares"/>)
    /// </summary>
    public class TzLocateHistoryRow
    {
        [JsonProperty("accountId")] public string AccountId { get; set; }
        [JsonProperty("quoteReqID")] public string QuoteReqId { get; set; }
        [JsonProperty("symbol")] public string Symbol { get; set; }
        [JsonProperty("locateStatus")] public int LocateStatus { get; set; }
        [JsonProperty("locateType")] public int LocateType { get; set; }
        [JsonProperty("locateShares")] public long LocateShares { get; set; }
        [JsonProperty("filledShares")] public long FilledShares { get; set; }
        [JsonProperty("locatePrice")] public decimal LocatePrice { get; set; }
        [JsonProperty("locateError")] public int LocateError { get; set; }
        [JsonProperty("text")] public string Text { get; set; }
        [JsonProperty("createdDate")] public DateTime? CreatedDate { get; set; }
        [JsonProperty("updatedDate")] public DateTime? UpdatedDate { get; set; }

        /// <summary>
        /// False while TradeZero is still working on the row: 81 (Quoting), or 54 (Pending) without an error.
        /// A 54 row with an error code is a refusal for that pool ("11 No shares available").
        /// </summary>
        [JsonIgnore]
        public bool IsSettled => LocateStatus != TzLocateStatus.Quoting &&
            !(LocateStatus == TzLocateStatus.Pending && LocateError == 0 && string.IsNullOrEmpty(Text));

        public override string ToString()
        {
            return $"{QuoteReqId} {Symbol} status={LocateStatus} type={LocateType} shares={LocateShares} filled={FilledShares} price={LocatePrice} error={LocateError} text={Text}";
        }
    }

    public class TzLocateHistoryResponse
    {
        [JsonProperty("locateHistory")] public List<TzLocateHistoryRow> LocateHistory { get; set; } = new();
    }

    /// <summary>
    /// A row of GET /accounts/{a}/locates/inventory: one per (symbol, locateType)
    /// </summary>
    public class TzLocateInventoryRow
    {
        [JsonProperty("accountId")] public string AccountId { get; set; }
        [JsonProperty("symbol")] public string Symbol { get; set; }
        [JsonProperty("locateType")] public int LocateType { get; set; }
        [JsonProperty("available")] public long Available { get; set; }
        [JsonProperty("unavailable")] public long Unavailable { get; set; }
        [JsonProperty("toBeSold")] public long ToBeSold { get; set; }
        [JsonProperty("sold")] public long Sold { get; set; }
    }

    public class TzLocateQuoteRequest
    {
        [JsonProperty("account")] public string Account { get; set; }
        [JsonProperty("symbol")] public string Symbol { get; set; }
        [JsonProperty("quantity")] public long Quantity { get; set; }
        [JsonProperty("quoteReqID")] public string QuoteReqId { get; set; }
    }

    public class TzLocateAcceptRequest
    {
        [JsonProperty("accountId")] public string AccountId { get; set; }
        [JsonProperty("quoteReqID")] public string QuoteReqId { get; set; }
    }

    public class TzLocateSellRequest
    {
        [JsonProperty("account")] public string Account { get; set; }
        [JsonProperty("symbol")] public string Symbol { get; set; }
        [JsonProperty("quantity")] public long Quantity { get; set; }
        [JsonProperty("locateType")] public string LocateType { get; set; }
        [JsonProperty("quoteReqID")] public string QuoteReqId { get; set; }
    }
}
