#!/bin/sh
set -e

if [ ! -x .venv/bin/python ]; then
    echo 'Creating Python virtual environment ".venv"...'
    python3 -m venv .venv
fi

echo 'Installing dependencies from "requirements.txt" into virtual environment (in quiet mode)...'
PYYAML_FORCE_LIBYAML=0 .venv/bin/python -m pip --quiet --disable-pip-version-check install -r app/backend/requirements.txt