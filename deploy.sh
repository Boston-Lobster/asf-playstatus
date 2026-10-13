#!/usr/bin/env bash
set -euo pipefail

# 用法: ./deploy.sh <服务器IP> <SSH密钥路径> [ASF插件目录]
# 默认把编译产物部署到 /opt/ASF/plugins/AsfPlayStatus/ 并重启 asf 服务

SERVER_IP="${1:?usage: ./deploy.sh <server-ip> <ssh-key> [asf-plugins-dir]}"
SSH_KEY="${2:?usage: ./deploy.sh <server-ip> <ssh-key> [asf-plugins-dir]}"
ASF_PLUGINS_DIR="${3:-/opt/ASF/plugins}"
ROOT="$(cd "$(dirname "$0")" && pwd)"
DLL="$ROOT/AsfPlayStatus/bin/Release/net10.0/AsfPlayStatus.dll"

if [[ ! -f "$DLL" ]]; then
  echo "未找到编译产物: $DLL，请先运行 build.ps1 / dotnet build" >&2
  exit 1
fi

ssh -i "$SSH_KEY" -o BatchMode=yes "root@$SERVER_IP" "mkdir -p '$ASF_PLUGINS_DIR/AsfPlayStatus'"
scp -i "$SSH_KEY" -o BatchMode=yes "$DLL" "root@$SERVER_IP:$ASF_PLUGINS_DIR/AsfPlayStatus/AsfPlayStatus.dll"
ssh -i "$SSH_KEY" -o BatchMode=yes "root@$SERVER_IP" "chown -R asf:asf '$ASF_PLUGINS_DIR/AsfPlayStatus' && systemctl restart asf && sleep 3 && systemctl is-active asf"
echo "部署完成: $ASF_PLUGINS_DIR/AsfPlayStatus/AsfPlayStatus.dll"

