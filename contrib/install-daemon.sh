#!/bin/sh
# Install the SharpClaw screen capture daemon as a systemd user service.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
SERVICE_NAME="sharpclaw-screen"

BINARY="$REPO_ROOT/src/SharpClaw/bin/Debug/net9.0/SharpClaw"
if [ ! -f "$BINARY" ]; then
    echo "Binary not found at $BINARY — building..."
    dotnet build "$REPO_ROOT/src/SharpClaw/SharpClaw.csproj" -c Debug -v quiet
fi

SERVICE_DIR="$HOME/.config/systemd/user"
mkdir -p "$SERVICE_DIR"

cat > "$SERVICE_DIR/$SERVICE_NAME.service" <<EOF
[Unit]
Description=SharpClaw Screen Capture Daemon
After=graphical-session.target

[Service]
Type=simple
ExecStart=$BINARY --daemon
Restart=on-failure
RestartSec=10
PassEnvironment=DISPLAY WAYLAND_DISPLAY XDG_RUNTIME_DIR

[Install]
WantedBy=default.target
EOF

echo "Installed $SERVICE_DIR/$SERVICE_NAME.service"

systemctl --user daemon-reload
systemctl --user enable "$SERVICE_NAME"
systemctl --user start "$SERVICE_NAME"

echo ""
echo "Screen daemon installed and started."
echo ""
echo "Useful commands:"
echo "  systemctl --user status $SERVICE_NAME    # check status"
echo "  journalctl --user -u $SERVICE_NAME -f    # follow logs"
echo "  systemctl --user stop $SERVICE_NAME      # stop"
echo "  systemctl --user disable $SERVICE_NAME   # disable autostart"
