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
    VOL="${3:-80}"
    if [ "$TARGET" = "--wait" ]; then TARGET=""; fi
    if [ "$VOL" = "--wait" ]; then VOL="80"; fi
    if [ "$VOL" = "current" ]; then VOL=""; fi
    check_wait_flag "$@"

    echo "Submitting instant chime (low-latency, no fade) [wait: ${WAIT_PARAM:-false}, vol: ${VOL:-current}]..."
    JSON_BODY=$(jq -n \
      --arg url "https://actions.google.com/sounds/v1/alarms/beep_short.ogg" \
      --arg target "$TARGET" \
      --arg vol "$VOL" \
      '{
        audioUrl: $url,
        fadeDurationMs: 0,
        pausePlayback: false
      }
      + (if $target != "" then { groupIds: [$target] } else {} end)
      + (if $vol != "" then { volume: ($vol | tonumber) } else {} end)')
    
    curl -s -X POST "$BASE_URL/api/broadcast/play$WAIT_PARAM" \
      -H "Content-Type: application/json" \
      -d "$JSON_BODY" | jq .
    ;;

  fading)
    TARGET="${2:-}"
    VOL="${3:-75}"
    if [ "$TARGET" = "--wait" ]; then TARGET=""; fi
    if [ "$VOL" = "--wait" ]; then VOL="75"; fi
    if [ "$VOL" = "current" ]; then VOL=""; fi
    check_wait_flag "$@"

    echo "Submitting gentle announcement with 1.5s volume fade-out and fade-in [wait: ${WAIT_PARAM:-false}, vol: ${VOL:-current}]..."
    JSON_BODY=$(jq -n \
      --arg url "https://actions.google.com/sounds/v1/alarms/bugle_tune.ogg" \
      --arg target "$TARGET" \
      --arg vol "$VOL" \
      '{
        audioUrl: $url,
        fadeDurationMs: 1500,
        timeoutSeconds: 15,
        pausePlayback: false
      }
      + (if $target != "" then { groupIds: [$target] } else {} end)
      + (if $vol != "" then { volume: ($vol | tonumber) } else {} end)')
    
    curl -s -X POST "$BASE_URL/api/broadcast/play$WAIT_PARAM" \
      -H "Content-Type: application/json" \
      -d "$JSON_BODY" | jq .
    ;;

  alarm)
    VOL="${2:-90}"
    if [ "$VOL" = "--wait" ]; then VOL="90"; fi
    check_wait_flag "$@"

    echo "Submitting alarm siren across ALL groups (whole-house alert) [wait: ${WAIT_PARAM:-false}, vol: $VOL]..."
    JSON_BODY=$(jq -n \
      --arg vol "$VOL" \
      '{
        audioUrl: "https://actions.google.com/sounds/v1/alarms/mechanical_clock_ring.ogg",
        fadeDurationMs: 0,
        timeoutSeconds: 20,
        interrupt: true,
        volume: ($vol | tonumber)
      }')

    curl -s -X POST "$BASE_URL/api/broadcast/play$WAIT_PARAM" \
      -H "Content-Type: application/json" \
      -d "$JSON_BODY" | jq .
    ;;

  upload)
    FILE="${2:-}"
    if [ -z "$FILE" ] || [ ! -f "$FILE" ]; then
      echo "Usage: $0 upload <path-to-audio-file> [groupId] [volume] [--wait]"
      exit 1
    fi
    TARGET="${3:-}"
    VOL="${4:-}"
    if [ "$TARGET" = "--wait" ]; then TARGET=""; fi
    if [ "$VOL" = "--wait" ]; then VOL=""; fi
    check_wait_flag "$@"

    echo "Uploading and streaming local audio file: $FILE [wait: ${WAIT_PARAM:-false}, vol: ${VOL:-current}]"
    CURL_ARGS=(
      -s -X POST "$BASE_URL/api/broadcast/play-file$WAIT_PARAM"
      -F "file=@$FILE"
      -F "fadeDurationMs=500"
    )
    if [ -n "$TARGET" ]; then CURL_ARGS+=(-F "groupIds=$TARGET"); fi
    if [ -n "$VOL" ]; then CURL_ARGS+=(-F "volume=$VOL"); fi

    curl "${CURL_ARGS[@]}" | jq .
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
    echo "  chime [groupId] [vol] [--wait]   Instant chime/doorbell (default: current group volume)"
    echo "  fading [groupId] [vol] [--wait]  Gentle clip with 1.5s volume fade (default: current group vol)"
    echo "  alarm [vol] [--wait]             Whole-house emergency alarm (default: vol 90)"
    echo "  upload <file> [grp] [vol] [--wait] Upload and stream local MP3/WAV directly"
    echo "  jobs [limit]                     List recent broadcast jobs from database"
    echo "  job <jobId>                      Inspect specific broadcast job details and status"
    echo "  cancel <jobId>                   Cancel a queued or active broadcast job"
    echo "  stop                             Stop and cancel active broadcast immediately"
    echo "  status                           Check whether a broadcast is currently playing"
    echo ""
    echo "Examples:"
    echo "  $0 fading                        # Plays with current group volume"
    echo "  $0 fading g1 80                  # Plays on group 'g1' at 80% volume"
    echo "  $0 chime Wohnzimmer --wait       # Chime on Wohnzimmer, waits for completion"
    echo "  $0 jobs                          # View history of recent broadcasts"
    ;;
esac
