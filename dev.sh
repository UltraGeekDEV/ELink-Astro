#!/usr/bin/env bash
# Memory-safe dotnet wrapper: no resident build servers (they once ate 12 GB), cleanup afterwards.
# usage: ./dev.sh build|test [args]
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 DOTNET_gcServer=0
cmd="$1"; shift
dotnet "$cmd" -nodeReuse:false -p:UseSharedCompilation=false "$@"; rc=$?
dotnet build-server shutdown >/dev/null 2>&1
exit $rc
