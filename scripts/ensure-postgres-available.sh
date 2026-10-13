#!/usr/bin/env bash
set -euo pipefail

: "${AZURE_SUBSCRIPTION_ID:?AZURE_SUBSCRIPTION_ID is required}"
: "${AZURE_RESOURCE_GROUP:?AZURE_RESOURCE_GROUP is required}"
mode="${POSTGRES_GUARD_MODE:-check}"
if [[ "$mode" != "check" && "$mode" != "recover" ]]; then
  echo "::error::POSTGRES_GUARD_MODE must be check or recover."
  exit 1
fi
if [[ "$mode" == "recover" && "${GITHUB_ACTIONS:-false}" == "true" && "${GITHUB_EVENT_NAME:-}" != "workflow_dispatch" ]]; then
  echo "::error::Temporary recovery requires an explicit manual workflow dispatch, never a scheduled or push-triggered run."
  exit 1
fi
max_attempts="${SERVER_READY_MAX_ATTEMPTS:-40}"
retry_delay="${SERVER_READY_RETRY_DELAY:-30}"
if ! [[ "$max_attempts" =~ ^[1-9][0-9]*$ && "$retry_delay" =~ ^[0-9]+$ ]]; then
  echo "::error::The readiness attempt count and retry delay must be valid integers."
  exit 1
fi

server_names_output=$(az postgres flexible-server list \
  --subscription "$AZURE_SUBSCRIPTION_ID" --resource-group "$AZURE_RESOURCE_GROUP" \
  --query "[].name" --only-show-errors --output tsv)
if [ -z "$server_names_output" ]; then
  echo "::error::No PostgreSQL Flexible Server was found in ${AZURE_RESOURCE_GROUP}."
  exit 1
fi
mapfile -t server_names <<< "$server_names_output"
if [ "${#server_names[@]}" -ne 1 ]; then
  echo "::error::Expected one PostgreSQL Flexible Server, found ${#server_names[@]}."
  exit 1
fi
server_name="${server_names[0]}"
server_id=$(az postgres flexible-server show \
  --subscription "$AZURE_SUBSCRIPTION_ID" --resource-group "$AZURE_RESOURCE_GROUP" \
  --name "$server_name" --query id --only-show-errors --output tsv)
if [ -z "$server_id" ]; then
  echo "::error::PostgreSQL resource discovery returned no resource ID."
  exit 1
fi

ensure_tag() {
  local value
  value=$(az postgres flexible-server show \
    --subscription "$AZURE_SUBSCRIPTION_ID" --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$server_name" --query tags.CostControl --only-show-errors --output tsv)
  if [ "$value" != "Ignore" ]; then
    az tag update --subscription "$AZURE_SUBSCRIPTION_ID" --resource-id "$server_id" \
      --operation Merge --tags CostControl=Ignore --only-show-errors --output none
    value=$(az postgres flexible-server show \
      --subscription "$AZURE_SUBSCRIPTION_ID" --resource-group "$AZURE_RESOURCE_GROUP" \
      --name "$server_name" --query tags.CostControl --only-show-errors --output tsv)
  fi
  if [ "$value" != "Ignore" ]; then
    echo "::error::PostgreSQL tag verification failed; CostControl must equal Ignore."
    exit 1
  fi
}

if [ "$mode" = "check" ]; then
  value=$(az postgres flexible-server show \
    --subscription "$AZURE_SUBSCRIPTION_ID" --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$server_name" --query tags.CostControl --only-show-errors --output tsv)
  state=$(az postgres flexible-server show \
    --subscription "$AZURE_SUBSCRIPTION_ID" --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$server_name" --query state --only-show-errors --output tsv)
  if [[ "${state,,}" != "ready" || "$value" != "Ignore" ]]; then
    echo "::error::PostgreSQL state=${state}, CostControl=${value:-missing}. Scheduled checks do not override cost governance. Review exemption eligibility and authorized recovery."
    exit 1
  fi
  echo "Verified: PostgreSQL is Ready and CostControl=Ignore. Read-only check; no renewal or restart."
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    printf '### PostgreSQL monitor\n- Server: `%s`\n- State: Ready\n- Tag: `CostControl=Ignore`\n- Read-only; this is not exemption renewal.\n' "$server_name" >> "$GITHUB_STEP_SUMMARY"
  fi
  exit 0
fi

echo "::warning::Explicit temporary recovery. Re-applying a tag does not renew a governance exemption or establish permanent availability."
ensure_tag
start_requested=false
for ((attempt = 1; attempt <= max_attempts; attempt++)); do
  state=$(az postgres flexible-server show \
    --subscription "$AZURE_SUBSCRIPTION_ID" --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$server_name" --query state --only-show-errors --output tsv)
  echo "PostgreSQL ${server_name} state: ${state}"
  case "${state,,}" in
    ready)
      ensure_tag
      echo "Verified: PostgreSQL is Ready and CostControl=Ignore."
      if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
        printf '### PostgreSQL guard\n- Server: `%s`\n- State: Ready\n- Verified tag: `CostControl=Ignore`\n' \
          "$server_name" >> "$GITHUB_STEP_SUMMARY"
      fi
      exit 0
      ;;
    stopped)
      if [ "$start_requested" = false ]; then
        az postgres flexible-server start \
          --subscription "$AZURE_SUBSCRIPTION_ID" --resource-group "$AZURE_RESOURCE_GROUP" \
          --name "$server_name" --no-wait --only-show-errors --output none
        start_requested=true
      fi
      ;;
    starting|stopping|updating)
      echo "Waiting for PostgreSQL to finish its state transition."
      ;;
    *)
      echo "::error::PostgreSQL cannot be recovered from state ${state}."
      exit 1
      ;;
  esac
  if [ "$attempt" -lt "$max_attempts" ]; then
    sleep "$retry_delay"
  fi
done
echo "::error::PostgreSQL did not reach Ready after ${max_attempts} checks."
exit 1
