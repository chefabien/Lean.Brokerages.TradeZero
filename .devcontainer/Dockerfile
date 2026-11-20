# Development image: the same runtime as production (quantconnect/lean ships the .NET 10 SDK and
# Python 3.11 with pythonnet), plus a non-root user matching the host so build outputs stay yours.
ARG LEAN_IMAGE=quantconnect/lean:latest
FROM ${LEAN_IMAGE}

ARG USERNAME=dev
ARG USER_UID=1000
ARG USER_GID=1000

RUN (getent group ${USER_GID} || groupadd --gid ${USER_GID} ${USERNAME}) \
 && (getent passwd ${USER_UID} || useradd --uid ${USER_UID} --gid ${USER_GID} -m -s /bin/bash ${USERNAME}) \
 && mkdir -p /home/${USERNAME}/.nuget/packages \
 && chown -R ${USER_UID}:${USER_GID} /home/${USERNAME}

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

# The lean image's entrypoint starts the engine; a dev container just idles.
ENTRYPOINT []
CMD ["sleep", "infinity"]
