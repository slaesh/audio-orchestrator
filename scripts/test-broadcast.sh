#!/usr/bin/env bash
set -e

BASE_URL="${ORCHESTRATOR_URL:-http://localhost:5000}"

echo "=== Audio Orchestrator Broadcast Test Client ==="
echo "Targeting Orchestrator at: $BASE_URL"
echo ""

case "$1" in
  status)
    echo "Querying broadcast status:"
    curl -s "$BASE_URL/api/broadcast/status" | jq .
    ;;

  chime)
    TARGET="${2:-}"
    echo "Playing instant chime (low-latency, no fade)..."
    JSON_BODY=$(jq -n \
      --arg url "https://actions.google.com/sounds/v1/alarms/beep_short.ogg" \
      --arg target "$TARGET" \
      '{
        audioUrl: $url,
        volume: 80,
        fadeDurationMs: 0,
        pausePlayback: false
      } + (if $target != "" then { groupIds: [$target] } else {} end)')
    
    curl -s -X POST "$BASE_URL/api/broadcast/play" \
      -H "Content-Type: application/json" \
      -d "$JSON_BODY" | jq .
    ;;

  fading)
    TARGET="${2:-}"
    echo "Playing gentle announcement with 1.5s volume fade-out and fade-in..."
    JSON_BODY=$(jq -n \
      --arg url "https://actions.google.com/sounds/v1/ambiences/daytime_forest_bonfire.ogg" \
      --arg target "$TARGET" \
      '{
        audioUrl: $url,
        volume: 75,
        fadeDurationMs: 1500,
        timeoutSeconds: 15,
        pausePlayback: false
      } + (if $target != "" then { groupIds: [$target] } else {} end)')
    
    curl -s -X POST "$BASE_URL/api/broadcast/play" \
      -H "Content-Type: application/json" \
      -d "$JSON_BODY" | jq .
    ;;

  alarm)
    echo "Playing alarm siren across ALL groups (whole-house alert)..."
    curl -s -X POST "$BASE_URL/api/broadcast/play" \
      -H "Content-Type: application/json" \
      -d '{
        "audioUrl": "https://actions.google.com/sounds/v1/alarms/mechanical_clock_ring.ogg",
        "volume": 90,
        "fadeDurationMs": 0,
        "timeoutSeconds: 20,
        "interrupt": true
      }' | jq .
    ;;

  upload)
    FILE="${2:-}"
    if [ -z "$FILE" ] || [ ! -f "$FILE" ]; then
      echo "Usage: $0 upload <path-to-audio-file> [groupId]"
      exit 1
    fi
    TARGET="${3:-}"
    echo "Uploading and playing local audio file: $FILE"
    curl -s -X POST "$BASE_URL/api/broadcast/play-file" \
      -F "file=@$FILE" \
      -F "groupIds=$TARGET" \
      -F "volume=80" \
      -F "fadeDurationMs=500" | jq .
    ;;

  stop)
    echo "Sending STOP signal to abort any active broadcast..."
    curl -s -X POST "$BASE_URL/api/broadcast/stop"
    echo "Broadcast stopped."
    ;;

  *)
    echo "Usage: $0 <command> [args]"
    echo ""
    echo "Commands:"
    echo "  chime [groupId]      Play instant chime/doorbell (0ms fade, vol 80)"
    echo "  fading [groupId]     Play gentle clip with 1.5s volume fade-in/out"
    echo "  alarm                Trigger whole-house emergency alarm (vol 90, interrupt)"
    echo "  upload <file> [grp]  Upload and stream a local MP3/WAV file directly"
    echo "  stop                 Stop and cancel any active broadcast immediately"
    echo "  status               Check whether a broadcast is currently playing"
    echo ""
    echo "Example: $0 chime Wohnzimmer"
    ;;
esac
