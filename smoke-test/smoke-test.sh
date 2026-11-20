#!/usr/bin/env bash
# End-to-end check of a plugin release asset, run the way users run it: the official quantconnect/lean image,
# the TradeZero plugin mounted by tradezero_live.py, tz-mock as the TradeZero API and Lean's FakeDataQueue as
# market data. The algorithm (smoke-test/algorithm/main.py) reads the account from Python, places a far limit order
# and cancels it.
#
#   smoke-test/smoke-test.sh dist/tradezero-plugin-lean<V>.zip     (quantconnect/lean:<V> must be pullable or local)
set -euo pipefail

ZIP=$(realpath "${1:?usage: $0 path/to/tradezero-plugin-lean<V>.zip}")
HERE=$(cd "$(dirname "$0")" && pwd)
WORK=$(mktemp -d)
cleanup() {
  docker rm -f tz-smoke-lean tz-smoke-mock >/dev/null 2>&1 || true
  docker network rm tz-smoke >/dev/null 2>&1 || true
  rm -rf "$WORK" 2>/dev/null || true
}
trap cleanup EXIT

docker network create tz-smoke >/dev/null
docker build -q -t tz-mock "$HERE/../tz-mock" >/dev/null
docker run -d --rm --name tz-smoke-mock --network tz-smoke tz-mock >/dev/null
for _ in $(seq 30); do
  docker exec tz-smoke-mock python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/mock/scenarios')" 2>/dev/null && break
  sleep 1
done

python3 "$HERE/../tradezero_live.py" --data-feed fake --algorithm "$HERE/algorithm" --plugin-zip "$ZIP" \
  --tradezero-api-url http://tz-smoke-mock:8000 --docker-network tz-smoke \
  --out "$WORK/run" --cache "$WORK/cache" --container-name tz-smoke-lean </dev/null

set +e
TZ_API_KEY_ID=smoke-key TZ_API_SECRET_KEY=smoke-secret timeout 300 "$WORK/run/run.sh" </dev/null > "$WORK/lean.log" 2>&1
status=$?
set -e

grep -E "SMOKE|TradeZero|ERROR" "$WORK/lean.log" | tail -40
if grep -q "SMOKE PASSED" "$WORK/lean.log"; then
  echo "SMOKE TEST PASSED"
  exit 0
fi
echo "SMOKE TEST FAILED (lean exit $status); end of the log:"
tail -80 "$WORK/lean.log"
exit 1
