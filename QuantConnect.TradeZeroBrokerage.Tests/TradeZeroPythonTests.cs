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

using System.Linq;
using NUnit.Framework;
using Python.Runtime;
using QuantConnect.Orders;
using QuantConnect.Python;

namespace QuantConnect.Brokerages.TradeZero.Tests
{
    /// <summary>
    /// The broker's margin figures, order properties and locates must be usable from a Python algorithm
    /// </summary>
    [TestFixture, Category("TzMock"), NonParallelizable]
    public class TradeZeroPythonTests
    {
        private const string Module = @"
from AlgorithmImports import *
from clr import AddReference
AddReference('QuantConnect.Brokerages.TradeZero')
from QuantConnect.Brokerages.TradeZero import TradeZeroAccount, TradeZeroBrokerageModel, TradeZeroOrderProperties, TradeZeroTimeInForce
from QuantConnect.Brokerages.TradeZero import TradeZeroLocates, TradeZeroLocateType, TradeZeroLocateStatus

def read_account():
    account = TradeZeroAccount.get_current()
    if account is None:
        assert TradeZeroAccount.CURRENT is None
        return None
    assert TradeZeroAccount.CURRENT.account_id == account.account_id
    return [account.account_id, str(account.account_type), float(account.buying_power), float(account.account_value),
            float(account.used_leverage), float(account.allowed_leverage), float(account.maintenance_margin), account.is_paper]

def model_account_id():
    model = TradeZeroBrokerageModel()
    return None if model.account is None else model.account.account_id

def extended_hours_properties():
    props = TradeZeroOrderProperties()
    props.outside_regular_trading_hours = True
    props.time_in_force = TimeInForce.GOOD_TIL_CANCELED
    props.route = 'SMART'
    return props

def locate_properties(seen):
    props = TradeZeroOrderProperties()
    def choose(quote):
        seen.append(len(quote.offers))
        return next((o for o in quote.offers if o.type == TradeZeroLocateType.PRE_BORROW and o.price_per_share <= 0.05), None)
    props.locate_handler = choose
    return props

def declining_properties():
    props = TradeZeroOrderProperties()
    props.locate_handler = lambda q: None
    props.suppress_odd_lot_locate_warning = True
    return props

def sell_back_unused(symbol):
    return [(r.type == TradeZeroLocateType.PRE_BORROW, r.status == TradeZeroLocateStatus.PENDING, r.quantity)
            for r in TradeZeroLocates.sell_back_unused(symbol)]

def available(symbol):
    return TradeZeroLocates.get_available(symbol)
";

        [Test]
        public void AccountMarginIsReadableFromPython()
        {
            TestSetup.EnsureInitialized();
            PythonInitializer.Initialize();
            using var h = new TradeZeroHarness();

            using (Py.GIL())
            {
                using var module = PyModule.FromString("tradezero_account_test", Module);
                using var values = module.GetAttr("read_account").Invoke();
                Assert.IsFalse(values.IsNone(), "TradeZeroAccount.current is None while connected");
                Assert.AreEqual(h.Brokerage.AccountId, values[0].As<string>());
                Assert.AreEqual("Paper", values[1].As<string>());
                Assert.AreEqual(1_000_000d, values[2].As<double>());
                Assert.AreEqual(1_000_000d, values[3].As<double>());
                Assert.IsTrue(values[7].As<bool>());

                Assert.AreEqual(h.Brokerage.AccountId, module.GetAttr("model_account_id").Invoke().As<string>());

                var properties = module.GetAttr("extended_hours_properties").Invoke().As<TradeZeroOrderProperties>();
                Assert.IsTrue(properties.OutsideRegularTradingHours);
                Assert.AreEqual("SMART", properties.Route);
            }

            h.Brokerage.Disconnect();
            using (Py.GIL())
            {
                using var module = PyModule.FromString("tradezero_account_test_after", Module);
                Assert.IsTrue(module.GetAttr("read_account").Invoke().IsNone());
            }
        }

        [Test]
        public void LocateHandlerAndLocateApiWorkFromPython()
        {
            TestSetup.EnsureInitialized();
            PythonInitializer.Initialize();
            using var h = new TradeZeroHarness("short_htb");
            var regsho = TradeZeroHarness.Equity("REGSHO");

            TradeZeroOrderProperties accepting, declining;
            PyObject seen;
            using (Py.GIL())
            {
                using var module = PyModule.FromString("tradezero_locate_test", Module);
                seen = new PyList();
                accepting = module.GetAttr("locate_properties").Invoke(seen).As<TradeZeroOrderProperties>();
                declining = module.GetAttr("declining_properties").Invoke().As<TradeZeroOrderProperties>();
            }
            Assert.IsNotNull(accepting.LocateHandler);
            Assert.IsTrue(declining.SuppressOddLotLocateWarning);

            // the handler runs on a connector thread: the GIL must be free here
            var order = new MarketOrder(regsho, -200, System.DateTime.UtcNow, properties: accepting);
            Assert.IsTrue(h.Place(order));
            h.WaitForStatus(order, OrderStatus.Filled);
            Assert.AreEqual(TradeZeroLocateType.PreBorrow, TradeZeroLocates.GetInventory(regsho).Single().Type);

            var declined = new MarketOrder(TradeZeroHarness.Equity("HTBX"), -150, System.DateTime.UtcNow, properties: declining);
            Assert.IsTrue(h.Place(declined));
            h.WaitForStatus(declined, OrderStatus.Invalid);
            Assert.IsFalse(h.Messages.Any(m => m.Code == "LocateOddLot"));

            var cover = new MarketOrder(regsho, 200, System.DateTime.UtcNow);
            Assert.IsTrue(h.Place(cover));
            h.WaitForStatus(cover, OrderStatus.Filled);
            using (Py.GIL())
            {
                using var module = PyModule.FromString("tradezero_locate_test_after", Module);
                Assert.AreEqual(2, seen[0].As<int>(), "both Reg SHO offers reached the Python handler");
                Assert.AreEqual(200, module.GetAttr("available").Invoke(regsho.ToPython()).As<long>());
                using var results = module.GetAttr("sell_back_unused").Invoke(regsho.ToPython());
                Assert.AreEqual(1, results.Length());
                Assert.IsTrue(results[0][0].As<bool>(), "type PRE_BORROW");
                Assert.IsTrue(results[0][1].As<bool>(), "status PENDING");
                Assert.AreEqual(200, results[0][2].As<long>());
                seen.Dispose();
            }
        }
    }
}
