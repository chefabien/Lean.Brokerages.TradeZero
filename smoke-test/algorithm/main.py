# End-to-end smoke test: Lean (live mode) -> TradeZero plugin -> tz-mock.
# Market data comes from Lean's FakeDataQueue, so prices are fake: never use this setup for real trading.
from AlgorithmImports import *
from clr import AddReference
AddReference("QuantConnect.Brokerages.TradeZero")
from QuantConnect.Brokerages.TradeZero import TradeZeroAccount, TradeZeroBrokerageModel


class TradeZeroSmokeTest(QCAlgorithm):

    def initialize(self):
        self.symbol = self.add_equity("AAPL", Resolution.SECOND).symbol
        self.ticket = None
        self.done = False
        self.debug(f"SMOKE brokerage model: {type(self.brokerage_model).__name__}")

    def on_data(self, data):
        if self.done:
            return
        account = TradeZeroAccount.get_current()
        if self.ticket is None:
            if account is None:
                raise Exception("SMOKE FAILED: TradeZeroAccount.get_current() is None while live")
            self.debug(f"SMOKE account {account.account_type}: value={account.account_value} "
                       f"bp={account.buying_power} allowed leverage={account.allowed_leverage}")
            self.debug(f"SMOKE lean: cash={self.portfolio.cash} margin remaining={self.portfolio.margin_remaining} "
                       f"can short 100 AAPL={self.shortable(self.symbol, -100)}")
            if not self.shortable(self.symbol, -100):
                raise Exception("SMOKE FAILED: AAPL is easy to borrow in the mock but reported not shortable")
            # far below the market: it rests until canceled
            self.ticket = self.limit_order(self.symbol, 1, 1.0)
            self.debug(f"SMOKE placed limit order {self.ticket.order_id}")
            return
        status = self.ticket.status
        if status == OrderStatus.SUBMITTED:
            self.debug(f"SMOKE broker ids {self.transactions.get_order_by_id(self.ticket.order_id).broker_id}")
            self.ticket.cancel("smoke test done")
        elif status == OrderStatus.CANCELED:
            self.debug("SMOKE PASSED: order submitted and canceled through TradeZero")
            self.done = True
            self.quit("smoke test complete")
        elif status in (OrderStatus.INVALID,):
            raise Exception(f"SMOKE FAILED: order status {status}")

    def on_order_event(self, order_event):
        self.debug(f"SMOKE order event: {order_event}")
