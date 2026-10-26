#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
feed="$PWD/artifacts"
project=tests/AotConsumer/AotConsumer.csproj
dotnet restore "$project" --source "$feed" --source https://api.nuget.org/v3/index.json
dotnet run --project "$project" -c Release --no-restore
dotnet publish "$project" -c Release -r linux-x64 --self-contained true -p:PublishTrimmed=true -p:PublishAot=true --source "$feed" --source https://api.nuget.org/v3/index.json -o artifacts/aot
./artifacts/aot/AotConsumer
