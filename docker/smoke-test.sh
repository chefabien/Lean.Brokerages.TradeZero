#!/usr/bin/env bash
# End-to-end check of the Docker image: Lean in live mode, TradeZero plugin, tz-mock as the TradeZero API,
# Lean's FakeDataQueue as market data. Places a far limit order, cancels it, reads TradeZeroAccount from Python.
#
#   docker build -t lean-tradezero .
#   docker/smoke-test.sh [image]
set -euo pipefail

IMAGE=${1:-lean-tradezero}
HERE=$(cd "$(dirname "$0")" && pwd)
WORK=$(mktemp -d)
trap 'docker rm -f tz-smoke-lean tz-smoke-mock >/dev/null 2>&1 || true; docker network rm tz-smoke >/dev/null 2>&1 || true; rm -rf "$WORK"' EXIT

docker network create tz-smoke >/dev/null
docker build -q -t tz-mock "$HERE/../tz-mock" >/dev/null
docker run -d --rm --name tz-smoke-mock --network tz-smoke tz-mock >/dev/null
for _ in $(seq 30); do
  docker exec tz-smoke-mock python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/mock/scenarios')" 2>/dev/null && break
  sleep 1
done

# runtime config: the template, pointed at the mock, with the fake data feed and the exchange forced open
# (so the test also runs outside market hours)
sed -e 's#"REPLACE-WITH-YOUR-DATA-QUEUE-HANDLER"#"QuantConnect.Lean.Engine.DataFeeds.Queues.FakeDataQueue"#' \
    -e 's#"tradezero-api-url": "https://webapi.tradezero.com"#"tradezero-api-url": "http://tz-smoke-mock:8000"#' \
    -e 's#"algorithm-type-name": "MyAlgorithm"#"algorithm-type-name": "TradeZeroSmokeTest"#' \
    -e 's#"environment": "live-tradezero",#"environment": "live-tradezero",\n  "force-exchange-always-open": true,#' \
    "$HERE/config.tradezero.json" > "$WORK/config.json"
mkdir -p "$WORK/results"

set +e
timeout 300 docker run --rm --name tz-smoke-lean --network tz-smoke \
  -v "$WORK/config.json:/config/config.json:ro" \
  -v "$HERE/smoke:/Algorithm:ro" \
  -v "$WORK/results:/Results" \
  -e TZ_API_KEY_ID=smoke-key -e TZ_API_SECRET_KEY=smoke-secret \
  "$IMAGE" --config /config/config.json --close-automatically true > "$WORK/lean.log" 2>&1
status=$?
set -e

grep -E "SMOKE|TradeZero|ERROR" "$WORK/lean.log" | grep -v "^\s*$" | tail -40
echo "--- API log lines: $(wc -l < "$WORK/results/tradezero-api.jsonl" 2>/dev/null || echo 0)"
if grep -q "SMOKE PASSED" "$WORK/lean.log"; then
  echo "SMOKE TEST PASSED"
  exit 0
fi
echo "SMOKE TEST FAILED (lean exit $status); full log:"; tail -80 "$WORK/lean.log"
exit 1
