#!/usr/bin/env bash
# Exercise the image, then replace its container using the same local volume.
set -euo pipefail
image=${1:?usage: bash scripts/check-container.sh IMAGE}
volume=$(docker volume create)
container=''
cleanup() {
  if [[ -n "$container" ]]; then docker rm -fv "$container" >/dev/null || true; fi
  docker volume rm "$volume" >/dev/null
}
trap cleanup EXIT
export PAPRIKA_SMOKE_TOKEN=$(python3 -c 'import secrets; print(secrets.token_hex(32))')
export paprika__Operations__Users__0__Token=$PAPRIKA_SMOKE_TOKEN
start() {
  container=$(docker run -d --cap-drop ALL --security-opt no-new-privileges \
    -p 127.0.0.1::8080 --mount "type=volume,src=$volume,dst=/data" \
    -e paprika__Operations__Users__0__Name=smoke \
    -e paprika__Operations__Users__0__Roles__0=administrator \
    -e paprika__Operations__Users__0__Token "$image")
  endpoint="http://$(docker port "$container" 8080/tcp)"
  [[ $(docker exec "$container" id -u) != 0 ]]
}
start
packet=$(python3 scripts/container-smoke.py "$endpoint")
docker stop --timeout 30 "$container" >/dev/null
docker rm -v "$container" >/dev/null
container=''
start
python3 scripts/container-smoke.py "$endpoint" --verify-only "$packet"
