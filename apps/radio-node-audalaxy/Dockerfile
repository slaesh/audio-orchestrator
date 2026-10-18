FROM alpine:3.20

RUN apk add --no-cache python3 py3-aiohttp ffmpeg ca-certificates

WORKDIR /app
COPY main.py .

ENTRYPOINT ["python3", "-u", "main.py"]
