# SPDX-License-Identifier: AGPL-3.0-or-later
# Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

# Deprecated: superseded by install.ps1 (which also handles the API key and uninstall).
# Kept so existing muscle memory / scripts keep working.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Write-Warning 'deploy.ps1 is deprecated - use install.ps1. Forwarding...'
& (Join-Path $root 'install.ps1') @args
