#!/bin/bash
# provision-wsl.sh — Provision WSL environment for OpenClaw Windows integration
# Run this after installing the OpenClaw gateway in WSL
# Usage: wsl bash -c "bash /path/to/provision-wsl.sh"

set -euo pipefail

echo "🦞 OpenClaw WSL Provisioning"
echo "============================"
echo ""

# 1. Check if wslview is already available
if command -v wslview &>/dev/null; then
    echo "✅ wslview already installed: $(which wslview)"
else
    echo "⚠️  wslview not found — installing..."
    install_shim=false

    # Try installing wslu package first (provides official wslview)
    if command -v apt-get &>/dev/null; then
        if apt-cache show wslu &>/dev/null 2>&1; then
            echo "   Installing wslu package..."
            if [ "$(id -u)" -eq 0 ]; then
                apt-get update -qq && apt-get install -y -qq wslu
            else
                sudo apt-get update -qq && sudo apt-get install -y -qq wslu
            fi

            if command -v wslview &>/dev/null; then
                echo "✅ wslview installed via wslu: $(which wslview)"
            else
                echo "⚠️  wslu installed but wslview not found, creating shim..."
                install_shim=true
            fi
        else
            echo "   wslu package not available in apt, creating shim..."
            install_shim=true
        fi
    else
        echo "   apt not available, creating shim..."
        install_shim=true
    fi

    # Fallback: install our PowerShell-based wslview shim
    if [ "$install_shim" = "true" ] || ! command -v wslview &>/dev/null; then
        SHIM_PATH="/usr/local/bin/wslview"
        echo "   Creating wslview shim at $SHIM_PATH..."

        SHIM_CONTENT='#!/bin/bash
# wslview shim — opens URLs on Windows from WSL via PowerShell
URL="$1"
if [ -z "$URL" ]; then
    echo "Usage: wslview <url|file>" >&2
    exit 1
fi
exec /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe \
    -NoProfile -NonInteractive -Command "Start-Process '"'"'$URL'"'"'"'

        if [ "$(id -u)" -eq 0 ]; then
            echo "$SHIM_CONTENT" > "$SHIM_PATH"
            chmod +x "$SHIM_PATH"
        else
            echo "$SHIM_CONTENT" | sudo tee "$SHIM_PATH" > /dev/null
            sudo chmod +x "$SHIM_PATH"
        fi

        if command -v wslview &>/dev/null; then
            echo "✅ wslview shim installed: $(which wslview)"
        else
            echo "❌ Failed to install wslview shim"
            exit 1
        fi
    fi
fi

# 2. Verify PowerShell is accessible (required for wslview shim and open package)
PS_PATH="/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
if [ -x "$PS_PATH" ]; then
    echo "✅ PowerShell accessible: $PS_PATH"
else
    echo "❌ PowerShell not accessible at $PS_PATH"
    echo "   The wslview shim requires PowerShell to open Windows browsers."
    exit 1
fi

# 3. Quick test: can we open a URL?
echo ""
echo "Testing browser launch capability..."
if wslview "https://example.com" 2>&1; then
    echo "✅ Browser launch test: SUCCESS"
else
    echo "⚠️  Browser launch test: returned non-zero (may still work)"
fi

echo ""
echo "🎉 WSL provisioning complete!"
echo ""
echo "The OpenClaw gateway can now open browser windows for:"
echo "  • GitHub Copilot device code authentication"
echo "  • OpenAI OAuth flows"
echo "  • Any provider that requires browser-based auth"
