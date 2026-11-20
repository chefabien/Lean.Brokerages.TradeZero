#!/usr/bin/env bash
# Runs once when the dev container is created: make sure the sibling Lean checkout exists, then restore.
set -euo pipefail

LEAN_DIR=/workspaces/lean-dev/Lean
if [ ! -f "$LEAN_DIR/QuantConnect.Lean.sln" ]; then
  echo "Lean checkout not found at $LEAN_DIR, cloning (shallow)..."
  git clone --depth 1 https://github.com/QuantConnect/Lean.git "$LEAN_DIR"
fi

dotnet restore QuantConnect.TradeZeroBrokerage.sln
echo "Ready: dotnet build QuantConnect.TradeZeroBrokerage.sln && dotnet test --filter TestCategory!=Paper"
