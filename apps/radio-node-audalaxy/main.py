#!/usr/bin/env python3
import asyncio
import json
import logging
import os
import re
import signal
import sys
import urllib.parse
import uuid
import aiohttp

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] [audalaxy-node]: %(message)s",
    stream=sys.stdout
)
logger = logging.getLogger("audalaxy-node")

STREAM_ID = os.environ.get("STREAM_ID", "").strip()
STREAM_URL = os.environ.get("STREAM_URL", "").strip()
FIFO_PATH = os.environ.get("FIFO_PATH", "").strip()
WEBHOOK_URL = os.environ.get("WEBHOOK_URL", "http://localhost:5000/api/internal/radio-events").strip()

if not STREAM_URL or not FIFO_PATH:
    logger.error("STREAM_URL and FIFO_PATH must be provided!")
    sys.exit(1)

ffmpeg_process: asyncio.subprocess.Process | None = None
running = True


def handle_signal(sig, frame):
    global running, ffmpeg_process
    logger.info(f"Received exit signal ({sig}), shutting down cleanly...")
    running = False
    if ffmpeg_process and ffmpeg_process.returncode is None:
        try:
            ffmpeg_process.terminate()
        except Exception:
            ffmpeg_process.kill()
    sys.exit(0)


signal.signal(signal.SIGINT, handle_signal)
signal.signal(signal.SIGTERM, handle_signal)


async def send_webhook(session: aiohttp.ClientSession, title: str | None, artist: str | None, cover_url: str | None, source: str = "audalaxy-ws"):
    if not WEBHOOK_URL or not STREAM_ID:
        logger.warning("Skipping webhook: WEBHOOK_URL or STREAM_ID is not configured.")
        return

    payload = {
        "streamId": STREAM_ID,
        "title": title,
        "artist": artist,
        "coverUrl": cover_url
    }
    logger.info(f"[{source.upper()}] Sending metadata webhook -> Title: '{title}', Artist: '{artist}', Cover: '{cover_url}'")
    try:
        async with session.post(WEBHOOK_URL, json=payload, timeout=aiohttp.ClientTimeout(total=5)) as resp:
            if resp.status == 200:
                logger.info(f"[{source.upper()}] Webhook delivered successfully (HTTP 200)")
            else:
                logger.warning(f"[{source.upper()}] Webhook responded with HTTP {resp.status}")
    except Exception as e:
        logger.warning(f"[{source.upper()}] Failed to post metadata webhook: {e}")


async def resolve_audalaxy_session(session: aiohttp.ClientSession, url: str):
    meta_id = str(uuid.uuid4())
    listener_id = "903b3b971bbb4744bfd0cbd187144972"
    sep = "&" if "?" in url else "?"
    url_with_tokens = f"{url}{sep}listenerid={listener_id}&metaid={meta_id}"

    headers = {
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36"
    }

    channel_id = None
    audio_url = url_with_tokens

    logger.info(f"Resolving stream session via fast 1-hop redirect: {url_with_tokens}")
    try:
        async with session.get(url_with_tokens, headers=headers, allow_redirects=False, timeout=aiohttp.ClientTimeout(total=5)) as resp:
            location = resp.headers.get("Location")
            if location:
                audio_url = location
                parsed = urllib.parse.urlparse(location)
                segment = parsed.path.lstrip("/")
                
                # Check for -mp3- or -aac- marker in segment
                parts = re.split(r"-(?:mp3|aac)-", segment, flags=re.IGNORECASE)
                if len(parts) > 1 and parts[0]:
                    channel_id = parts[0]
                else:
                    channel_id = segment.split("-")[0].split("?")[0]

                logger.info(f"Discovered channel ID from redirect: '{channel_id}' (Redirect URL: {location[:85]}...)")
            else:
                logger.info("No redirect Location header returned, using original tokenized URL.")
    except Exception as ex:
        logger.warning(f"Channel discovery redirect check failed: {ex}. Using default tokens.")

    if not channel_id or "." in channel_id:
        channel_id = "sunsl_vpuji0s9d_ygld"
        logger.info(f"Using default fallback channel ID: '{channel_id}'")

    return meta_id, channel_id, audio_url


async def run_websocket_listener(session: aiohttp.ClientSession, meta_id: str, channel_id: str):
    ws_url = f"wss://10010.playerservices.audalaxy.io/listener/{meta_id}?channel={channel_id}"
    logger.info(f"Starting Audalaxy WebSocket listener on: {ws_url}")

    reconnect_attempt = 0
    while running:
        reconnect_attempt += 1
        try:
            logger.info(f"Connecting to Audalaxy WebSocket (attempt {reconnect_attempt})...")
            async with session.ws_connect(ws_url, heartbeat=25, timeout=aiohttp.ClientTimeout(total=15)) as ws:
                logger.info(f"Audalaxy WebSocket connected to channel '{channel_id}'. Waiting for song/ad events (may take 2-3 mins until next song transition)...")
                reconnect_attempt = 0

                async for msg in ws:
                    if not running:
                        break

                    if msg.type == aiohttp.WSMsgType.TEXT:
                        logger.info(f"Audalaxy WS RAW message: {msg.data}")
                        try:
                            data = json.loads(msg.data)
                            msg_type = data.get("type")
                            if msg_type in ("now", "track", "ad"):
                                title = data.get("song") or data.get("name")
                                artist = data.get("artist") or data.get("artistName")
                                cover = data.get("cover") or data.get("imageUrl")
                                if msg_type == "ad" or (not title and not artist):
                                    title = "Advertisement"
                                    artist = "Sunshine Live"

                                logger.info(f"Audalaxy Track Event [{msg_type}]: '{artist}' - '{title}' (Cover: {cover})")
                                await send_webhook(session, title, artist, cover, source="audalaxy-ws")
                            else:
                                logger.info(f"Audalaxy non-track message [{msg_type}]: {data}")
                        except Exception as parse_err:
                            logger.warning(f"Failed to parse WebSocket JSON: {parse_err}")

                    elif msg.type in (aiohttp.WSMsgType.CLOSED, aiohttp.WSMsgType.CLOSE):
                        logger.warning(f"Audalaxy WebSocket closed by remote server: {msg}")
                        break
                    elif msg.type == aiohttp.WSMsgType.ERROR:
                        logger.warning(f"Audalaxy WebSocket error: {ws.exception()}")
                        break

        except asyncio.CancelledError:
            logger.info("Audalaxy WebSocket listener cancelled.")
            break
        except Exception as e:
            if not running:
                break
            logger.warning(f"Audalaxy WebSocket connection failed ({e}). Reconnecting in 5s...")
            await asyncio.sleep(5)


async def run_ffmpeg_pipeline(session: aiohttp.ClientSession, audio_url: str):
    global ffmpeg_process, running

    ffmpeg_cmd = [
        "ffmpeg",
        "-hide_banner",
        "-nostats",
        "-loglevel", "info",
        "-user_agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36",
        "-reconnect", "1",
        "-reconnect_at_eof", "1",
        "-reconnect_streamed", "1",
        "-reconnect_delay_max", "5",
        "-rw_timeout", "10000000",
        "-thread_queue_size", "4096",
        "-i", audio_url,
        "-f", "s16le",
        "-acodec", "pcm_s16le",
        "-ac", "2",
        "-ar", "44100",
        "-y", FIFO_PATH
    ]

    while running:
        logger.info(f"Starting FFmpeg pipeline: '{audio_url[:75]}...' -> '{FIFO_PATH}'")
        ffmpeg_process = await asyncio.create_subprocess_exec(
            *ffmpeg_cmd,
            stderr=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.DEVNULL
        )

        last_stream_title = None

        async def monitor_stderr():
            nonlocal last_stream_title
            while running and ffmpeg_process and ffmpeg_process.returncode is None:
                line = await ffmpeg_process.stderr.readline()
                if not line:
                    break
                text = line.decode("utf-8", errors="ignore").strip()
                if not text:
                    continue

                if "StreamTitle" in text:
                    logger.info(f"FFmpeg ICY header: {text}")
                    match = re.search(r"StreamTitle\s*(?::|=)\s*['\"]?([^';\"]+)['\"]?", text, re.IGNORECASE)
                    if match:
                        raw_title = match.group(1).strip()
                        if raw_title and raw_title != last_stream_title:
                            last_stream_title = raw_title
                            artist = None
                            title = raw_title
                            if " - " in raw_title:
                                parts = raw_title.split(" - ", 1)
                                artist = parts[0].strip()
                                title = parts[1].strip()

                            logger.info(f"Found ICY StreamTitle: '{artist}' - '{title}' (Forwarding as instant fallback)")
                            await send_webhook(session, title, artist, None, source="ffmpeg-icy")
                elif any(k in text.lower() for k in ("error", "failed", "invalid")):
                    logger.warning(f"FFmpeg stderr: {text}")
                elif any(k in text for k in ("Output #0", "Stream mapping", "icy-name", "icy-description")):
                    logger.info(f"FFmpeg info: {text}")

        stderr_task = asyncio.create_task(monitor_stderr())

        returncode = await ffmpeg_process.wait()
        stderr_task.cancel()
        if running:
            logger.warning(f"FFmpeg process exited with code {returncode}. Restarting in 2s...")
            await asyncio.sleep(2)


async def main():
    logger.info(f"=== Starting radio-node-audalaxy ===")
    logger.info(f"Stream ID:   {STREAM_ID}")
    logger.info(f"Stream URL:  {STREAM_URL}")
    logger.info(f"FIFO Path:   {FIFO_PATH}")
    logger.info(f"Webhook URL: {WEBHOOK_URL}")

    async with aiohttp.ClientSession() as session:
        meta_id, channel_id, audio_url = await resolve_audalaxy_session(session, STREAM_URL)
        logger.info(f"Session established: MetaID={meta_id}, ChannelID={channel_id}")

        ws_task = asyncio.create_task(run_websocket_listener(session, meta_id, channel_id))
        ffmpeg_task = asyncio.create_task(run_ffmpeg_pipeline(session, audio_url))

        try:
            await asyncio.gather(ws_task, ffmpeg_task)
        except asyncio.CancelledError:
            pass
        finally:
            ws_task.cancel()
            ffmpeg_task.cancel()
            if ffmpeg_process and ffmpeg_process.returncode is None:
                ffmpeg_process.terminate()


if __name__ == "__main__":
    asyncio.run(main())
