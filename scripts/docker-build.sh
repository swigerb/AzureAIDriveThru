#!/bin/bash

# Ensure frontend .env file exists before building Docker image
if [ -f "./app/frontend/.env" ]; then
  echo "✅ Frontend .env file found"
    echo "(Not printing contents.)"
else
    echo "⚠️ Frontend .env file not found, creating from sample or backend..."
    
    # Try to copy from backend env variables related to auth
    if [ -f "./app/backend/.env" ]; then
        echo "Creating frontend .env from backend VITE_* variables"
        grep -E '^VITE_[A-Za-z0-9_]*=' ./app/backend/.env > ./app/frontend/.env 2>/dev/null || echo "" > ./app/frontend/.env
        
        # If no VITE_ variables found, add the defaults
        if [ ! -s "./app/frontend/.env" ]; then
            echo "# Authentication Settings" > ./app/frontend/.env
            echo "VITE_AUTH_URL=YOUR_AUTH_URL_HERE" >> ./app/frontend/.env
            echo "VITE_AUTH_ENABLED=false" >> ./app/frontend/.env
            echo "Added default authentication settings to frontend .env (please update VITE_AUTH_URL)"
        fi
    else
        # Create a new .env file with default values
        echo "# Authentication Settings" > ./app/frontend/.env
        echo "VITE_AUTH_URL=YOUR_AUTH_URL_HERE" >> ./app/frontend/.env
        echo "VITE_AUTH_ENABLED=false" >> ./app/frontend/.env
        echo "Created new frontend .env with default settings (please update VITE_AUTH_URL)"
    fi
fi

# Build the Docker image (frontend config is read from app/frontend/.env)
echo "🔨 Building Docker image..."
# Issue #129: the build context is the repo root, the same as CI and azd (azure.yaml docker.context).
docker build --no-cache -t sonic-drive-thru-app -f ./app/Dockerfile .

# Run the container
echo "🚀 Running Docker container..."
docker run -p 8000:8000 --env-file ./app/backend/.env sonic-drive-thru-app:latest
