#!/usr/bin/env bash
set -euo pipefail

DOTNET_INSTALL_DIR="${DOTNET_INSTALL_DIR:-$HOME/dotnet}"
ASPNETCORE_DIR="${ASPNETCORE_DIR:-$HOME/aspnetcore}"

echo "==> Downloading official .NET SDK (LTS)"
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
/tmp/dotnet-install.sh --channel LTS --install-dir "$DOTNET_INSTALL_DIR"
export PATH="$DOTNET_INSTALL_DIR:$PATH"
echo "    Installed: $(dotnet --version 2>/dev/null || echo 'check PATH')"

echo "==> Cloning ASP.NET Core source (latest main)"
if [ -d "$ASPNETCORE_DIR/.git" ]; then
  echo "    Already cloned, pulling latest..."
  git -C "$ASPNETCORE_DIR" pull --ff-only
else
  git clone --depth=1 https://github.com/dotnet/aspnetcore.git "$ASPNETCORE_DIR"
fi

echo ""
echo "Done."
echo "  ASP.NET Core source : $ASPNETCORE_DIR"
echo "  .NET SDK            : $DOTNET_INSTALL_DIR"
echo ""
echo "Add to PATH:  export PATH=\"$DOTNET_INSTALL_DIR:\$PATH\""
