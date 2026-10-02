#!/bin/sh
set -eu

app_uid=${MOENOTES_UID:-65532}
app_gid=${MOENOTES_GID:-65532}

run_app() {
    exec gosu "$app_uid:$app_gid" dotnet /app/MoenotesAssets.dll "$@"
}

if [ "${VPNGATE_ENABLED:-0}" != "1" ]; then
    run_app "$@"
fi

if [ "$(id -u)" -ne 0 ]; then
    echo "[vpngate] VPNGATE_ENABLED=1 requires a root entrypoint" >&2
    exit 1
fi

tun_device=${VPNGATE_TUN:-tun0}
config=${VPNGATE_CONFIG:-/etc/openvpn/vpngate-jp.conf}
username=${VPNGATE_USERNAME:-vpn}
password=${VPNGATE_PASSWORD:-vpn}
remote=${VPNGATE_REMOTE:-219.100.37.177}
port=${VPNGATE_PORT:-443}
targets=${VPNGATE_TARGETS:-api.bang-dream-on.jp,static.bang-dream-on.jp}
connect_timeout=${VPNGATE_CONNECT_TIMEOUT:-60}
route_refresh=${VPNGATE_ROUTE_REFRESH:-30}
auth_file=/run/vpngate-auth

if [ ! -c /dev/net/tun ]; then
    echo "[vpngate] /dev/net/tun is unavailable; add NET_ADMIN and the TUN device" >&2
    exit 1
fi
if [ ! -r "$config" ]; then
    echo "[vpngate] OpenVPN config not found: $config" >&2
    exit 1
fi

umask 077
printf '%s\n%s\n' "$username" "$password" > "$auth_file"

cleanup() {
    trap - EXIT INT TERM
    if [ "${app_pid:-}" != "" ]; then kill "$app_pid" 2>/dev/null || true; fi
    if [ "${vpn_pid:-}" != "" ]; then kill "$vpn_pid" 2>/dev/null || true; fi
    if [ "${app_pid:-}" != "" ]; then wait "$app_pid" 2>/dev/null || true; fi
    if [ "${vpn_pid:-}" != "" ]; then wait "$vpn_pid" 2>/dev/null || true; fi
    rm -f "$auth_file"
}
trap cleanup EXIT INT TERM

echo "[vpngate] starting remote=$remote port=$port targets=$targets"
openvpn \
    --config "$config" \
    --remote "$remote" "$port" \
    --auth-user-pass "$auth_file" \
    --auth-nocache \
    --route-nopull \
    --remote-cert-tls server \
    --verb "${VPNGATE_VERB:-3}" &
vpn_pid=$!

seconds=0
while ! ip link show "$tun_device" >/dev/null 2>&1; do
    if ! kill -0 "$vpn_pid" 2>/dev/null; then
        wait "$vpn_pid" 2>/dev/null || true
        echo "[vpngate] OpenVPN exited before the tunnel was ready" >&2
        exit 1
    fi
    if [ "$seconds" -ge "$connect_timeout" ]; then
        echo "[vpngate] tunnel did not become ready within ${connect_timeout}s" >&2
        exit 1
    fi
    seconds=$((seconds + 1))
    sleep 1
done

refresh_routes() {
    routes_added=0
    old_ifs=$IFS
    IFS=,
    for host in $targets; do
        [ -n "$host" ] || continue
        ips=$(getent ahostsv4 "$host" 2>/dev/null | awk '{print $1}' | sort -u || true)
        for ip_address in $ips; do
            case "$ip_address" in
                ''|*[!0-9.]*) continue ;;
            esac
            if ip route replace "$ip_address/32" dev "$tun_device"; then
                routes_added=$((routes_added + 1))
            fi
        done
    done
    IFS=$old_ifs
    [ "$routes_added" -gt 0 ]
}

if ! refresh_routes; then
    echo "[vpngate] no JP target resolved; refusing to start without split routes" >&2
    exit 1
fi
echo "[vpngate] tunnel ready on $tun_device; JP routes installed"

gosu "$app_uid:$app_gid" dotnet /app/MoenotesAssets.dll "$@" &
app_pid=$!
route_seconds=0
while :; do
    if ! kill -0 "$vpn_pid" 2>/dev/null; then
        echo "[vpngate] OpenVPN exited; stopping the application" >&2
        exit 1
    fi
    if ! kill -0 "$app_pid" 2>/dev/null; then
        if wait "$app_pid"; then status=0; else status=$?; fi
        exit "$status"
    fi
    route_seconds=$((route_seconds + 5))
    if [ "$route_seconds" -ge "$route_refresh" ]; then
        refresh_routes || true
        route_seconds=0
    fi
    sleep 5
done
