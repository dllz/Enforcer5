#!/bin/bash
# enf - Enforcer server management script
# Usage: enf <command> [args...]

set -euo pipefail

BASE=/opt/enforcer
CONFIGS=(normal premium)

if [[ -t 1 ]]; then
    GREEN='\033[0;32m'; RED='\033[0;31m'; BOLD='\033[1m'; RESET='\033[0m'
else
    GREEN=''; RED=''; BOLD=''; RESET=''
fi

valid_config() {
    local c="$1"
    for v in "${CONFIGS[@]}"; do [[ "$c" == "$v" ]] && return 0; done
    return 1
}

dll_for() {
    case "$1" in
        normal)  echo "Enforcer.dll" ;;
        premium) echo "Enforcer Premium.dll" ;;
    esac
}

version_for() {
    local config="$1"
    local dll="$BASE/$config/App/$(dll_for "$config")"
    [[ -f "$dll" ]] || { echo "not found"; return; }
    strings "$dll" 2>/dev/null | grep -oE '^5\.0\.[0-9]{4}\.[0-9]+$' | head -1 || echo "?"
}

cmd_status() {
    local target="${1:-}"
    for config in "${CONFIGS[@]}"; do
        [[ -n "$target" && "$target" != "$config" ]] && continue
        local svc="enforcer-${config}"
        local state
        state=$(systemctl is-active "$svc" 2>/dev/null || true)
        local ver; ver=$(version_for "$config")
        if [[ "$state" == "active" ]]; then
            echo -e "${BOLD}$config${RESET}: v$ver ${GREEN}[RUNNING]${RESET}"
        else
            echo -e "${BOLD}$config${RESET}: v$ver ${RED}[$state]${RESET}"
        fi
        local backup="$BASE/$config/App/Backup"
        if [[ -d "$backup" ]] && compgen -G "$backup/*.dll" > /dev/null; then
            echo "  backup present"
        fi
    done
}

cmd_service() {
    local action="$1" target="${2:-}"
    if [[ -z "$target" ]]; then
        echo "Usage: enf $action <config|all>" >&2; exit 1
    fi
    if [[ "$target" == "all" ]]; then
        for config in "${CONFIGS[@]}"; do
            systemctl "$action" "enforcer-${config}" && echo "  $action enforcer-${config}"
        done
    else
        valid_config "$target" || { echo "Unknown config: $target" >&2; exit 1; }
        systemctl "$action" "enforcer-${target}" && echo "  $action enforcer-${target}"
    fi
}

cmd_logs() {
    local config="${1:-}" which="${2:-service}"
    valid_config "$config" || { echo "Usage: enf logs <normal|premium> [service|app]" >&2; exit 1; }
    case "$which" in
        service) journalctl -u "enforcer-${config}" -f --no-pager -n 100 ;;
        app)     tail -f "$BASE/$config/Logs"/*.log ;;
        *)       echo "Usage: enf logs <config> [service|app]" >&2; exit 1 ;;
    esac
}

cmd_help() {
    cat <<'EOF'
enf - Enforcer server management

  status [config]              show version and service state
  start|stop|restart <config|all>
  logs <config> [service|app]  follow journald (service) or the tmpfs log files (app)
  help

Configs: normal, premium
EOF
}

cmd="${1:-help}"; shift || true
case "$cmd" in
    help|--help|-h) cmd_help ;;
    status)         cmd_status "${1:-}" ;;
    start|stop|restart) cmd_service "$cmd" "${1:-}" ;;
    logs)           cmd_logs "${1:-}" "${2:-}" ;;
    *)
        echo "Unknown command: $cmd" >&2
        echo "Run 'enf help' for usage." >&2
        exit 1
        ;;
esac
