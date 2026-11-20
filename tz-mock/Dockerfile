# tz-mock as a container (used by the dev container's docker-compose and by CI).
FROM python:3.14-slim

RUN pip install --no-cache-dir "fastapi>=0.115" "uvicorn>=0.30" "python-multipart>=0.0.9"

WORKDIR /app
COPY tz_mock ./tz_mock
COPY data ./data

ENV TZ_MOCK_HOST=0.0.0.0 \
    TZ_MOCK_PORT=8000 \
    PYTHONUNBUFFERED=1
EXPOSE 8000

HEALTHCHECK --interval=2s --timeout=2s --retries=15 \
  CMD python -c "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8000/mock/scenarios')"

CMD ["python", "-m", "tz_mock"]
