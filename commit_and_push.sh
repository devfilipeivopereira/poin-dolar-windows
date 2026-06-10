#!/usr/bin/env bash
# Auto-commit and push all changes for GARCH fix
set -e
git add .
git commit -m "Fix GARCH sigma calculations, UI updates, installer improvements"
git push origin main
