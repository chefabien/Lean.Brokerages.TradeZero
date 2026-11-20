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

using NUnit.Framework;
using Python.Runtime;
using QuantConnect.Python;

namespace QuantConnect.Brokerages.TradeZero.Tests
{
    /// <summary>
    /// The broker's margin figures must be readable from a Python algorithm
    /// </summary>
    [TestFixture, Category("TzMock"), NonParallelizable]
    public class TradeZeroPythonTests
    {
        private const string Module = @"
from AlgorithmImports import *
from clr import AddReference
AddReference('QuantConnect.Brokerages.TradeZero')
from QuantConnect.Brokerages.TradeZero import TradeZeroAccount, TradeZeroBrokerageModel, TradeZeroOrderProperties, TradeZeroTimeInForce

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
    }
}
