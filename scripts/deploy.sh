#!/usr/bin/env bash
# Deploy amicus-api to a Pi environment: publish -> migrate -> restart -> health.
#
#   scripts/deploy.sh stage    # -> amicus-api-stage.service on :5091, amicus_stage DB
#   scripts/deploy.sh prod     # -> amicus-api.service       on :5090, amicus_prod  DB
#
# Run by the GitHub Actions self-hosted runner (user thorsp) and usable by hand.
# The env files hold the connection string + secrets and are NOT in git.
set -euo pipefail

ENVNAME="${1:-}"
case "$ENVNAME" in
  stage) SERVICE=amicus-api-stage; APPDIR="$HOME/apps/amicus-api-stage"; ENVFILE="$HOME/.config/amicus/api-stage.env"; PORT=5091 ;;
  prod)  SERVICE=amicus-api;       APPDIR="$HOME/apps/amicus-api";       ENVFILE="$HOME/.config/amicus/api.env";       PORT=5090 ;;
  *) echo "usage: deploy.sh <stage|prod>"; exit 2 ;;
esac

[ -r "$ENVFILE" ] || { echo "ERROR: env file missing: $ENVFILE"; exit 3; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

echo "--- deploy $ENVNAME (service=$SERVICE port=$PORT) ---"
echo "commit: $(git rev-parse --short HEAD) on $(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo detached)"

echo "--- publish ---"
dotnet publish src/Amicus.Api -c Release -o "$APPDIR" --nologo

echo "--- migrate ($ENVNAME db) ---"
# The env file's connection string points ef at the right database. Quoted values
# (the connection string, FromName) require this shell-safe sourcing.
set -a; . "$ENVFILE"; set +a
dotnet tool restore >/dev/null
ASPNETCORE_ENVIRONMENT=Production dotnet dotnet-ef database update \
  -p src/Amicus.Infrastructure -s src/Amicus.Api

echo "--- restart ---"
sudo systemctl restart "$SERVICE.service"

echo "--- health ---"
for i in $(seq 1 30); do
  code="$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PORT/health" || true)"
  if [ "$code" = "200" ]; then echo "healthy after ${i}s"; exit 0; fi
  sleep 1
done
echo "ERROR: $SERVICE did not become healthy on :$PORT"
sudo systemctl --no-pager status "$SERVICE.service" 2>&1 | tail -15 || true
exit 1
