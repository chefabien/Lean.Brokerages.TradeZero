# LEAN + TradeZero brokerage plugin.
#
#   docker build -t lean-tradezero .
#   docker build --build-arg LEAN_IMAGE=quantconnect/lean:18274 -t lean-tradezero:18274 .
#
# The plugin is compiled against the Lean binaries of the very image it is added to (LeanBinDir),
# so plugin and engine can never disagree on versions. Only QuantConnect.Brokerages.TradeZero.dll is
# added: no market-data feed is embedded. Choose one at runtime (see docker/README.md).
ARG LEAN_IMAGE=quantconnect/lean:latest

FROM ${LEAN_IMAGE} AS build
WORKDIR /src
COPY QuantConnect.TradeZeroBrokerage/ QuantConnect.TradeZeroBrokerage/
RUN dotnet build QuantConnect.TradeZeroBrokerage/QuantConnect.TradeZeroBrokerage.csproj \
      -c Release -p:LeanBinDir=/Lean/Launcher/bin/Debug -o /out \
 && test -f /out/QuantConnect.Brokerages.TradeZero.dll

FROM ${LEAN_IMAGE}
LABEL org.opencontainers.image.title="lean-tradezero" \
      org.opencontainers.image.description="QuantConnect LEAN with the TradeZero brokerage plugin (REST API)"
COPY --from=build /out/QuantConnect.Brokerages.TradeZero.dll \
                  /out/QuantConnect.Brokerages.TradeZero.pdb \
                  /out/QuantConnect.Brokerages.TradeZero.xml \
                  /Lean/Launcher/bin/Debug/
# Extra plugins (market-data feeds...) can be mounted here; enable them with "plugin-directory"
RUN mkdir -p /Lean/plugins
