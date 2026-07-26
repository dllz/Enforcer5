#!/bin/bash
# Enforcer Linux Server Setup
# Run as root: sudo bash deploy/setup.sh

set -e
BASE=/opt/enforcer
CONFIGS="normal premium"

echo "=== Creating directory structure ==="
for config in $CONFIGS; do
    mkdir -p $BASE/$config/App/Update
    mkdir -p $BASE/$config/App/Backup
    # Outside App/ so a deploy (which replaces App/) cannot destroy uploaded translations.
    mkdir -p $BASE/$config/TempLanguageFiles
    mkdir -p $BASE/$config/Logs
done

echo "=== Installing systemd services ==="

# Each config reads a different API token, chosen at compile time by DefineConstants.
declare -A APP_DLL
APP_DLL[normal]="Enforcer.dll"
APP_DLL[premium]="Enforcer Premium.dll"

declare -A API_ENV
API_ENV[normal]="EnforcerAPI"
API_ENV[premium]="EnforcerPremiumAPI"

for config in $CONFIGS; do
    SERVICE="enforcer-${config}.service"
    DLL="${APP_DLL[$config]}"
    APIKEY="${API_ENV[$config]}"
    cat > /etc/systemd/system/$SERVICE << EOF
[Unit]
Description=Enforcer (${config})
After=network.target

[Service]
Type=simple
WorkingDirectory=$BASE/$config/App
ExecStart=/usr/bin/dotnet "$BASE/$config/App/$DLL"
Restart=always
RestartSec=5
Environment=${APIKEY}=
Environment=TelegramServerUrl=
Environment=RedisConnection=
Environment=RedisPassword=
Environment=PaymentProviderToken=
Environment=ErrorChatId=
Environment=DisplayTimeZone=Europe/Amsterdam
Environment=LogPath=$BASE/$config/Logs
Environment=LanguagesPath=$BASE/$config/App/Languages
Environment=TempLanguageFilesPath=$BASE/$config/TempLanguageFiles

[Install]
WantedBy=multi-user.target
EOF
    echo "  -> $SERVICE installed (DLL: $DLL, API env: $APIKEY)"
done

systemctl daemon-reload

echo "=== Setting up tmpfs mounts for logs (RAM-backed, 50MB each) ==="
for config in $CONFIGS; do
    MOUNT="$BASE/$config/Logs"
    if ! grep -q "$MOUNT" /etc/fstab; then
        echo "tmpfs $MOUNT tmpfs size=50m,nodev,nosuid,noexec 0 0" >> /etc/fstab
        echo "  -> Added fstab entry for $MOUNT"
    else
        echo "  -> $MOUNT already in fstab"
    fi
    mount "$MOUNT" 2>/dev/null || true
done
echo "  Logs directories are now RAM-backed (tmpfs). Data lost on reboot - that's intentional."

echo "=== Installing enf management script ==="
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cp "$SCRIPT_DIR/enf.sh" /usr/local/bin/enf
chmod +x /usr/local/bin/enf
echo "  -> enf installed to /usr/local/bin/enf"

echo ""
echo "=== Setup complete ==="
echo ""
echo "Next steps:"
echo "1. Edit each /etc/systemd/system/enforcer-*.service"
echo "   Set the API token, RedisConnection, RedisPassword, ErrorChatId, PaymentProviderToken"
echo ""
echo "2. Deploy a build via the deploy bot: /upgradeenforcer normal"
echo ""
echo "3. Enable services:"
echo "   systemctl enable --now enforcer-normal"
echo "   systemctl enable --now enforcer-premium"
echo ""
echo "4. Check status:"
echo "   enf status"
echo "   journalctl -u enforcer-normal -f"
