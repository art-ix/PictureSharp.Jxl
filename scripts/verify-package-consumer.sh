#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
project=tests/AotConsumer/AotConsumer.csproj
dotnet restore "$project"
dotnet run --project "$project" -c Release --no-restore
dotnet publish "$project" -c Release -r linux-x64 --self-contained true -p:PublishTrimmed=true -p:PublishAot=true -o artifacts/aot
./artifacts/aot/AotConsumer
