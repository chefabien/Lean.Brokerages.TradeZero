# tz-mock as a container (used by the dev container's docker-compose, by CI and by the smoke test).
#
#   docker build -t tz-mock .                                   the server (last stage)
#   docker build --target test -t tz-mock-test . && docker run --rm tz-mock-test   tz-mock's own tests

FROM python:3.14-slim AS base

# Dependencies are installed outside /app: the dev container mounts the live code over /app.
COPY requirements.txt /tmp/
RUN pip install --no-cache-dir --root-user-action=ignore -r /tmp/requirements.txt

WORKDIR /app
COPY tz_mock ./tz_mock
COPY data ./data
ENV PYTHONUNBUFFERED=1


FROM base AS test

COPY requirements-test.txt /tmp/
RUN pip install --no-cache-dir --root-user-action=ignore -r /tmp/requirements-test.txt
COPY pyproject.toml ./
COPY tests ./tests
ENV PYTHONDONTWRITEBYTECODE=1
CMD ["python", "-m", "pytest", "-q", "-p", "no:cacheprovider"]


FROM base AS runtime

ENV TZ_MOCK_HOST=0.0.0.0 \
    TZ_MOCK_PORT=8000
EXPOSE 8000

HEALTHCHECK --interval=2s --timeout=2s --retries=15 \
  CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/mock/scenarios')"

CMD ["python", "-m", "tz_mock"]
