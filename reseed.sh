#!/usr/bin/env bash
# Reset the eSkrb API to a freshly seeded state.
#
# The API rebuilds its schema and reseeds on startup (api/Data/Seeder.cs) whenever the SQLite
# file is missing, so "reseed" = back up and remove the persisted state, then (optionally) start.
#
# Usage: ./reseed.sh [--demo] [--run] [--force]
#   --demo   seed the DEMO profile (user-secret Seed:Profile=demo): 10 patients at different stages of recovery, each with a
#            history up to yesterday and NO check-in today (the judges do it), all paired as buddies. Default = the dev profile
#            (4 patients incl. today's check-ins). The chosen profile is remembered in user-secrets until the next reseed.
#   --run    start the API afterwards (dotnet run --project api)
#   --force  don't refuse when something is listening on the API port
#
# Stable tokens: the login tokens are written in this file (TOKENS below). Every reseed copies them
# into `dotnet user-secrets` for api/, which the API loads in the Development environment, so any way
# of starting it (dotnet watch, IDE, --run) accepts the same tokens. To change one, edit it here.
# An env var Seed__Tokens__<key> still overrides (env beats user-secrets).
# NOTE: these are real credentials for the public instance; the data is fake, but treat accordingly.
#
# Env overrides:
#   DB_PATH   SQLite file       (default: api/data/eskrb.db; matches ConnectionStrings__Default)
#   PORT      API port to check (default: 5000)
#   MEDIA_PATH photo directory  (default: <DB dir>/media; matches Media:Path)
#
# What counts as persisted state (KEEP THIS LIST IN SYNC when adding any):
#   - SQLite DB (+ -wal/-shm)              : users, tokens, protocols, groups, memberships, episodes, check-ins, flags
#   - user-secrets Seed:Tokens:*           : (every key in TOKENS: the patients of both profiles, doctor, senior, admin) copied from TOKENS in this file on every run
#   - user-secrets Seed:Profile            : dev | demo, set on every run from --demo
#   - photo directory (MEDIA_PATH)         : patient photos (DiskMediaStore); deleted on reseed since the DB rows are gone
#   - push devices (PushDevices table)       : phones registered for push. The wipe would unlink them, so before removing the DB this
#     script exports them to <DB dir>/preserved-push-devices.json (needs the sqlite3 CLI) and the API re-imports them on its next
#     start (matching users keep their seeded ids). Not kept: if sqlite3 is missing (a warning is printed).
#   - user-secrets Soniox:ApiKey (NOT in this file, it is a real paid key): never touched by reseed; set it once with
#     `dotnet user-secrets set Soniox:ApiKey <key> --project api`
#   - user-secrets OpenAI:ApiKey (NOT in this file, paid key): never touched by reseed; set once with
#     `dotnet user-secrets set OpenAI:ApiKey <key> --project api`
#   (the day a bucket replaces the disk store, this list and cleanup must change)

set -euo pipefail
cd "$(dirname "$0")"

DB_PATH="${DB_PATH:-api/data/eskrb.db}"
PORT="${PORT:-5000}"
MEDIA_PATH="${MEDIA_PATH:-$(dirname "$DB_PATH")/media}"
# Stable login tokens, one per seeded user (key = Key in api/Data/Seeder.cs). Add new seeded users here.
declare -A TOKENS=(
  [ana]=esk_9YnthHuR9f0LGLiUQN5BI9iXI6MhmIDNW-GzigMQgBk
  [marko]=esk_zWgDY7oV3_a2JWn3M15NQ7HOPzQ-UeDxw5FoWKOkUiA
  [luka]=esk_Y1KYKXxdtHGnMsg5FsOw_P3elccSz49LeVV2Hit1Yfk
  [nina]=esk_j_x6sre0oSNl8Rx3ZhQQ1DcFVGM-ku3rcWfpCfJM9R8
  [senior]=esk_foEp0Pcr3f8WY_oc0otnDLUAcB6C5z9321uh2grmS24
  [doctor]=esk_MjaRlQT6s2xdhDu8hlwSl3Ci0HWDoM1Mndn-6lTNurk
  [admin]=esk_-gbkKf_ZfnQNrdRNO47mfknlNt-u76TPqA0dBKn_gpU
  [janez]=esk_BeXqRa9AEUv73gmpD7PbybtFjlpCdvfGIZjpoKqVpzo
  [marija]=esk_1raFxhIU76cMmoA8cbJxRJ2aM9lylN2wD8B4K5kz_eE
  [franc]=esk_fGBaQ473EqL0I8N1SG3-F8Mw9om3CdesS2cmokH78Vk
  [ivana]=esk_F4Y1TF1yKKRTGj_OhG8rD4KI9GpTrfqlEHOeAKk-Jzk
  [tone]=esk_YcE_zYBj5i3FISCEdMgDZER8eFEPXoNIks00YMnNUK4
  [milena]=esk_QE1LRpIkKSdGTWrOqCKsJK6mqOvoiotnxo5F2kpBNbY
)
RUN=0
FORCE=0
PROFILE=dev
for arg in "$@"; do
  case "$arg" in
    --demo) PROFILE=demo ;;
    --run) RUN=1 ;;
    --force) FORCE=1 ;;
    -h|--help) sed -n '2,/^set -e/p' "$0" | sed '$d'; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

# Copy the tokens above into user-secrets so every way of starting the API uses them.
set_tokens() {
  local key
  for key in "${!TOKENS[@]}"; do
    dotnet user-secrets set "Seed:Tokens:$key" "${TOKENS[$key]}" --project api >/dev/null
  done
  echo "Stable tokens set for: ${!TOKENS[*]}"
}

# Don't pull the DB out from under a running API (the live instance lives on this port).
if [ "$FORCE" -eq 0 ] && command -v ss >/dev/null && ss -ltn "sport = :$PORT" 2>/dev/null | grep -q LISTEN; then
  echo "Something is listening on :$PORT. Stop the API first, or pass --force." >&2
  exit 1
fi

# Keep phones linked to their users: export the push devices before the DB is wiped (the API re-imports them on start).
preserved="$(dirname "$DB_PATH")/preserved-push-devices.json"
if [ -e "$DB_PATH" ]; then
  if command -v sqlite3 >/dev/null; then
    rows="$(sqlite3 -json "$DB_PATH" 'SELECT Id, UserId, ExpoToken, Platform, DeviceId, CreatedAt, UpdatedAt FROM PushDevices' 2>/dev/null || true)"
    if [ -n "$rows" ] && [ "$rows" != "[]" ]; then
      printf '%s\n' "$rows" > "$preserved"
      echo "Kept $(printf '%s' "$rows" | grep -o '"ExpoToken"' | wc -l) push device(s) -> $preserved (re-imported on the next API start)"
    fi
  else
    echo "WARNING: sqlite3 not found, registered push devices will NOT survive this reseed (phones re-register on app start)." >&2
  fi
fi

backup_dir="$(dirname "$DB_PATH")/backups"
if [ -e "$DB_PATH" ]; then
  mkdir -p "$backup_dir"
  ts="$(date +%Y%m%d-%H%M%S)"
  mv "$DB_PATH" "$backup_dir/$(basename "$DB_PATH").$ts"
  echo "Backed up $DB_PATH -> $backup_dir/$(basename "$DB_PATH").$ts"
  # Keep only the 3 most recent backups.
  ls -1t "$backup_dir" | tail -n +4 | while read -r old; do rm -f -- "$backup_dir/$old"; done
else
  echo "No existing DB at $DB_PATH"
fi
rm -f -- "$DB_PATH-wal" "$DB_PATH-shm"
if [ -n "$MEDIA_PATH" ] && [ "$MEDIA_PATH" != "/" ] && [ -d "$MEDIA_PATH" ]; then
  rm -rf -- "$MEDIA_PATH"
  echo "Removed photo directory $MEDIA_PATH"
fi

set_tokens
dotnet user-secrets set "Seed:Profile" "$PROFILE" --project api >/dev/null
echo "Seed profile: $PROFILE"

if [ "$RUN" -eq 1 ]; then
  echo "Starting API (fresh DB is created and seeded on startup)..."
  exec dotnet run --project api
else
  echo "Done. The next API start rebuilds and reseeds the DB."
fi
