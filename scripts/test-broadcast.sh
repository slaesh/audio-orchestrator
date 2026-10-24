#!/usr/bin/env bash
set -e

BASE_URL="${ORCHESTRATOR_URL:-http://localhost:5000}"

echo "=== Audio Orchestrator Broadcast Test Client ==="
echo "Targeting Orchestrator at: $BASE_URL"
echo ""

WAIT_PARAM=""
ASYNC_MODE=0

for arg in "$@"; do
  if [ "$arg" = "--wait" ]; then
    WAIT_PARAM="?wait=true"
  elif [ "$arg" = "--async" ]; then
    ASYNC_MODE=1
  fi
done

poll_job_until_done() {
  local job_id="$1"
  if [ -z "$job_id" ] || [ "$job_id" = "null" ]; then
    return
  fi

  echo "Tracking job $job_id..."
  local elapsed=0
  while true; do
    sleep 2
    elapsed=$((elapsed + 2))
    local response
    response=$(curl -s -w "\n%{http_code}" "$BASE_URL/api/broadcast/jobs/$job_id" || echo -e "{}\n000")
    local http_code
    http_code=$(echo "$response" | tail -n 1)
    local job_json
    job_json=$(echo "$response" | sed '$d')

    if [ "$http_code" != "200" ]; then
      echo "✗ Failed to query job $job_id (HTTP $http_code): $job_json" >&2
      return 1
    fi

    local status
    status=$(echo "$job_json" | jq -r '.status // "unknown"')
    local err
    err=$(echo "$job_json" | jq -r '.errorMessage // ""')
    local dur
    dur=$(echo "$job_json" | jq -r '.durationMs // 0')

    case "$status" in
      completed)
        echo "✓ Job completed successfully in ${dur}ms!"
        echo "$job_json" | jq .
        break
        ;;
      cancelled)
        echo "⚠ Job was cancelled after ${dur}ms (Message: ${err:-None})"
        echo "$job_json" | jq .
        break
        ;;
      failed)
        echo "✗ Job FAILED after ${dur}ms: $err"
        echo "$job_json" | jq .
        break
        ;;
      playing)
        echo "  [${elapsed}s] Playing announcement on target zone(s)..."
        ;;
      queued)
        echo "  [${elapsed}s] Queued, waiting for playback worker..."
        ;;
      *)
        echo "  [${elapsed}s] Status: $status"
        ;;
    esac
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

  watch)
    JOB_ID="${2:-}"
    if [ -z "$JOB_ID" ]; then
      echo "Usage: $0 watch <job-id>"
      exit 1
    fi
    poll_job_until_done "$JOB_ID"
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
    if [ "$TARGET" = "--wait" ] || [ "$TARGET" = "--async" ]; then TARGET=""; fi
    if [ "$VOL" = "--wait" ] || [ "$VOL" = "--async" ]; then VOL="80"; fi
    if [ "$VOL" = "current" ]; then VOL=""; fi

    echo "Submitting instant chime [vol: ${VOL:-current}]..."
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
    
    RESP=$(curl -s -X POST "$BASE_URL/api/broadcast/play$WAIT_PARAM" \
      -H "Content-Type: application/json" \
      -d "$JSON_BODY")
    
    echo "$RESP" | jq .
    JOB_ID=$(echo "$RESP" | jq -r '.id // empty')

    if [ -n "$JOB_ID" ] && [ -z "$WAIT_PARAM" ] && [ "$ASYNC_MODE" -eq 0 ]; then
      poll_job_until_done "$JOB_ID"
    fi
    ;;

  fading)
    TARGET="${2:-}"
    VOL="${3:-75}"
    if [ "$TARGET" = "--wait" ] || [ "$TARGET" = "--async" ]; then TARGET=""; fi
    if [ "$VOL" = "--wait" ] || [ "$VOL" = "--async" ]; then VOL="75"; fi
    if [ "$VOL" = "current" ]; then VOL=""; fi

    echo "Submitting gentle announcement with 1.5s volume fade [vol: ${VOL:-current}]..."
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
    
    RESP=$(curl -s -X POST "$BASE_URL/api/broadcast/play$WAIT_PARAM" \
      -H "Content-Type: application/json" \
      -d "$JSON_BODY")
    
    echo "$RESP" | jq .
    JOB_ID=$(echo "$RESP" | jq -r '.id // empty')

    if [ -n "$JOB_ID" ] && [ -z "$WAIT_PARAM" ] && [ "$ASYNC_MODE" -eq 0 ]; then
      poll_job_until_done "$JOB_ID"
    fi
    ;;

  alarm)
    VOL="${2:-90}"
    if [ "$VOL" = "--wait" ] || [ "$VOL" = "--async" ]; then VOL="90"; fi

    echo "Submitting alarm siren across ALL groups [vol: $VOL]..."
    JSON_BODY=$(jq -n \
      --arg vol "$VOL" \
      '{
        audioUrl: "https://actions.google.com/sounds/v1/alarms/mechanical_clock_ring.ogg",
        fadeDurationMs: 0,
        timeoutSeconds: 20,
        interrupt: true,
        volume: ($vol | tonumber)
      }')

    RESP=$(curl -s -X POST "$BASE_URL/api/broadcast/play$WAIT_PARAM" \
      -H "Content-Type: application/json" \
      -d "$JSON_BODY")

    echo "$RESP" | jq .
    JOB_ID=$(echo "$RESP" | jq -r '.id // empty')

    if [ -n "$JOB_ID" ] && [ -z "$WAIT_PARAM" ] && [ "$ASYNC_MODE" -eq 0 ]; then
      poll_job_until_done "$JOB_ID"
    fi
    ;;

  upload)
    FILE="${2:-}"
    if [ -z "$FILE" ] || [ ! -f "$FILE" ]; then
      echo "Usage: $0 upload <path-to-audio-file> [groupId] [volume] [--wait|--async]"
      exit 1
    fi
    TARGET="${3:-}"
    VOL="${4:-}"
    if [ "$TARGET" = "--wait" ] || [ "$TARGET" = "--async" ]; then TARGET=""; fi
    if [ "$VOL" = "--wait" ] || [ "$VOL" = "--async" ]; then VOL=""; fi

    echo "Uploading and streaming local audio file: $FILE [vol: ${VOL:-current}]"
    CURL_ARGS=(
      -s -X POST "$BASE_URL/api/broadcast/play-file$WAIT_PARAM"
      -F "file=@$FILE"
      -F "fadeDurationMs=500"
    )
    if [ -n "$TARGET" ]; then CURL_ARGS+=(-F "groupIds=$TARGET"); fi
    if [ -n "$VOL" ]; then CURL_ARGS+=(-F "volume=$VOL"); fi

    RESP=$(curl "${CURL_ARGS[@]}")
    echo "$RESP" | jq .
    JOB_ID=$(echo "$RESP" | jq -r '.id // empty')

    if [ -n "$JOB_ID" ] && [ -z "$WAIT_PARAM" ] && [ "$ASYNC_MODE" -eq 0 ]; then
      poll_job_until_done "$JOB_ID"
    fi
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
    echo "  chime [groupId] [vol] [--async]    Instant chime (polls every 2s until done)"
    echo "  fading [groupId] [vol] [--async]   Gentle announcement (polls every 2s until done)"
    echo "  alarm [vol] [--async]              Emergency siren (polls every 2s until done)"
    echo "  upload <file> [grp] [vol]          Upload & play file (polls every 2s until done)"
    echo "  watch <jobId>                      Poll status of existing job every 2s until done"
    echo "  jobs [limit]                       List recent broadcast jobs from database"
    echo "  job <jobId>                        Inspect specific broadcast job once"
    echo "  cancel <jobId>                     Cancel a queued or active broadcast job"
    echo "  stop                               Stop active broadcast immediately"
    echo "  status                             Check live playback status"
    echo ""
    echo "Examples:"
    echo "  $0 fading                          # Submits and monitors every 2s until finished"
    echo "  $0 fading g1 75 --async            # Submits and returns immediately"
    echo "  $0 watch 355e60c4...               # Monitor an existing job"
    echo "  $0 jobs 5                          # Show last 5 jobs"
    ;;
esac
