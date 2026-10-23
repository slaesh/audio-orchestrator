#!/usr/bin/env bash
set -e

BASE_URL="${ORCHESTRATOR_URL:-http://localhost:5000}"

echo "=== Audio Orchestrator Broadcast Test Client ==="
echo "Targeting Orchestrator at: $BASE_URL"
echo ""

WAIT_PARAM=""
check_wait_flag() {
  for arg in "$@"; do
    if [ "$arg" = "--wait" ]; then
      WAIT_PARAM="?wait=true"
      return
    fi
  done
}

case "$1" in
  status)
    echo "Querying broadcast status:"
    curl -s "$BASE_URL/api/broadcast/status" | jq .
    ;;

  jobs)
    LIMIT="${2:-20}"
    echo "Listing recent broadcast jobs (limit: $LIMIT):"
    curl -s "$BASE_URL/api/broadcast/jobs?limit=$LIMIT" | jq .
    ;;

  job)
    JOB_ID="${2:-}"
    if [ -z "$JOB_ID" ]; then
      echo "Usage: $0 job <job-id>"
      exit 1
    fi
    echo "Inspecting broadcast job: $JOB_ID"
    curl -s "$BASE_URL/api/broadcast/jobs/$JOB_ID" | jq .
    ;;

  cancel)
    JOB_ID="${2:-}"
    if [ -z "$JOB_ID" ]; then
      echo "Usage: $0 cancel <job-id>"
      exit 1
    fi
    echo "Cancelling broadcast job: $JOB_ID"
    curl -s -X POST "$BASE_URL/api/broadcast/jobs/$JOB_ID/cancel" | jq .
    ;;

  chime)
    TARGET="${2:-}"
    if [ "$TARGET" = "--wait" ]; then TARGET=""; fi
    check_wait_flag "$@"

    echo "Submitting instant chime (low-latency, no fade) [wait: ${WAIT_PARAM:-false}]..."
    JSON_BODY=$(jq -n \
      --arg url "https://actions.google.com/sounds/v1/alarms/beep_short.ogg" \
      --arg target "$TARGET" \
      '{
        audioUrl: $url,
        volume: 80,
        fadeDurationMs: 0,
        pausePlayback: false
      } + (if $target != "" then { groupIds: [$target] } else {} end)')
    
    curl -s -X POST "$BASE_URL/api/broadcast/play$WAIT_PARAM" \
      -H "Content-Type: application/json" \
      -d "$JSON_BODY" | jq .
    ;;

  fading)
    TARGET="${2:-}"
    if [ "$TARGET" = "--wait" ]; then TARGET=""; fi
    check_wait_flag "$@"

    echo "Submitting gentle announcement with 1.5s volume fade-out and fade-in [wait: ${WAIT_PARAM:-false}]..."
    JSON_BODY=$(jq -n \
      --arg url "https://actions.google.com/sounds/v1/alarms/bugle_tune.ogg" \
      --arg target "$TARGET" \
      '{
        audioUrl: $url,
        volume: 75,
        fadeDurationMs: 1500,
        timeoutSeconds: 15,
        pausePlayback: false
      } + (if $target != "" then { groupIds: [$target] } else {} end)')
    
    curl -s -X POST "$BASE_URL/api/broadcast/play$WAIT_PARAM" \
      -H "Content-Type: application/json" \
      -d "$JSON_BODY" | jq .
    ;;

  alarm)
    check_wait_flag "$@"
    echo "Submitting alarm siren across ALL groups (whole-house alert) [wait: ${WAIT_PARAM:-false}]..."
    curl -s -X POST "$BASE_URL/api/broadcast/play$WAIT_PARAM" \
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
      echo "Usage: $0 upload <path-to-audio-file> [groupId] [--wait]"
      exit 1
    fi
    TARGET="${3:-}"
    if [ "$TARGET" = "--wait" ]; then TARGET=""; fi
    check_wait_flag "$@"

    echo "Uploading and streaming local audio file: $FILE [wait: ${WAIT_PARAM:-false}]"
    curl -s -X POST "$BASE_URL/api/broadcast/play-file$WAIT_PARAM" \
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
    echo "  chime [groupId] [--wait]     Play instant chime/doorbell (0ms fade, vol 80)"
    echo "  fading [groupId] [--wait]    Play gentle clip with 1.5s volume fade-in/out"
    echo "  alarm [--wait]               Trigger whole-house emergency alarm (vol 90, interrupt)"
    echo "  upload <file> [grp] [--wait] Upload and stream a local MP3/WAV file directly"
    echo "  jobs [limit]                 List recent broadcast jobs from database"
    echo "  job <jobId>                  Inspect a specific broadcast job details and status"
    echo "  cancel <jobId>               Cancel a queued or active broadcast job"
    echo "  stop                         Stop and cancel active broadcast immediately"
    echo "  status                       Check whether a broadcast is currently playing"
    echo ""
    echo "Example: $0 chime Wohnzimmer"
    echo "Example: $0 chime Wohnzimmer --wait"
    echo "Example: $0 jobs"
    ;;
esac
