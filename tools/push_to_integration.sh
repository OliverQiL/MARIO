#!/usr/bin/env bash
# Sync this repo (the AR visualizer) into the team's integration repository, under visualizer/.
#
# Uses `git subtree --squash`: the integration repo gets the real files in visualizer/ (teammates
# and TAs can browse them without access to this repo), and each sync adds one squashed commit
# that names the MARIO commit it came from. Only *committed* work is synced.
#
# First time:   tools/push_to_integration.sh https://github.com/<org>/<integration-repo>.git
# Afterwards:   tools/push_to_integration.sh
#               (the URL is remembered in this repo's git config: integration.url)
#
# Options (environment variables):
#   INTEGRATION_PREFIX   folder in the integration repo        (default: visualizer)
#   INTEGRATION_BRANCH   branch to push to                      (default: main)
#   INTEGRATION_DIR      local working clone of the integration repo
#                        (default: ~/.cache/mario-integration)
#   DRY_RUN=1            do everything except the final push
set -euo pipefail

MARIO_DIR="$(cd "$(dirname "$0")/.." && pwd)"
PREFIX="${INTEGRATION_PREFIX:-visualizer}"
BRANCH="${INTEGRATION_BRANCH:-main}"
CLONE="${INTEGRATION_DIR:-$HOME/.cache/mario-integration}"

say()  { printf '\033[1;36m==>\033[0m %s\n' "$*"; }
fail() { printf '\033[1;31mxx\033[0m %s\n' "$*" >&2; exit 1; }

# --- where to push -----------------------------------------------------------------------
if [[ $# -ge 1 ]]; then
  git -C "$MARIO_DIR" config integration.url "$1"
fi
URL="$(git -C "$MARIO_DIR" config --get integration.url || true)"
[[ -n "$URL" ]] || fail "No integration repo set. Run once with its URL: tools/push_to_integration.sh https://github.com/<org>/<repo>.git"

# --- what to push: the committed state of MARIO's main -----------------------------------
[[ -z "$(git -C "$MARIO_DIR" status --porcelain)" ]] || fail "MARIO has uncommitted changes. Commit (or stash) first; only committed work is synced."
SRC_BRANCH="$(git -C "$MARIO_DIR" rev-parse --abbrev-ref HEAD)"
[[ "$SRC_BRANCH" == "main" ]] || fail "MARIO is on '$SRC_BRANCH'. Switch to main to sync."
SRC_COMMIT="$(git -C "$MARIO_DIR" rev-parse --short HEAD)"
SRC_SUBJECT="$(git -C "$MARIO_DIR" log -1 --format=%s)"

# --- a working clone of the integration repo, up to date ----------------------------------
if [[ ! -d "$CLONE/.git" ]]; then
  say "Cloning $URL into $CLONE"
  mkdir -p "$(dirname "$CLONE")"
  git clone "$URL" "$CLONE"
fi
cd "$CLONE"
[[ "$(git remote get-url origin)" == "$URL" ]] || git remote set-url origin "$URL"
git fetch origin
if git show-ref --verify --quiet "refs/remotes/origin/$BRANCH"; then
  git checkout -q "$BRANCH" 2>/dev/null || git checkout -q -b "$BRANCH" "origin/$BRANCH"
  git reset -q --hard "origin/$BRANCH"     # this clone is only a sync workspace: match the remote exactly
else
  say "Integration repo has no '$BRANCH' yet: it will be created"
  git checkout -q --orphan "$BRANCH" 2>/dev/null || true
fi

# --- bring the visualizer in ---------------------------------------------------------------
MSG="Visualizer: sync from MARIO $SRC_COMMIT ($SRC_SUBJECT)"
if [[ -d "$PREFIX" ]]; then
  say "Updating $PREFIX/ to MARIO $SRC_COMMIT"
  git subtree pull --prefix="$PREFIX" "$MARIO_DIR" main --squash -m "$MSG"
else
  say "Adding $PREFIX/ from MARIO $SRC_COMMIT (first sync)"
  if ! git rev-parse -q --verify HEAD >/dev/null; then
    git commit -q --allow-empty -m "Start integration branch"   # subtree add needs an existing commit
  fi
  git subtree add --prefix="$PREFIX" "$MARIO_DIR" main --squash -m "$MSG"
fi

# --- publish --------------------------------------------------------------------------------
if [[ "${DRY_RUN:-0}" == "1" ]]; then
  say "DRY_RUN: not pushing. Result is in $CLONE (branch $BRANCH)"
  git log --oneline -3
  exit 0
fi
say "Pushing to $URL ($BRANCH)"
git push origin "$BRANCH"
say "Done: $PREFIX/ in the integration repo now matches MARIO $SRC_COMMIT"
