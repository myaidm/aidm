#!/usr/bin/env bash
# Linux build + test script. The WPF presentation project targets
# Windows and cannot build on Linux, so it is excluded here; it
# builds on Windows with: dotnet build AiDownloadManager.slnx
#
# Tests run through AiDownloadManager.TestRunner (in-process xUnit
# runner), NOT `dotnet test`: the vstest testhost talks to
# vstest.console over TCP, and this sandbox blocks .NET TCP
# connections, so `dotnet test` can never connect here. The runner
# executes the same [Fact] tests in-process. On a machine with TCP
# allowed, `dotnet test` works as well.
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

dotnet build src/AiDownloadManager.Core/AiDownloadManager.Core.csproj -c Release
dotnet build src/AiDownloadManager.Protocols/AiDownloadManager.Protocols.csproj -c Release
dotnet build src/AiDownloadManager.Engine/AiDownloadManager.Engine.csproj -c Release
dotnet build src/AiDownloadManager.Infrastructure/AiDownloadManager.Infrastructure.csproj -c Release
dotnet build src/AiDownloadManager.Application/AiDownloadManager.Application.csproj -c Release
dotnet build src/AiDownloadManager.TestServer/AiDownloadManager.TestServer.csproj -c Release
dotnet build src/AiDownloadManager.Prototype/AiDownloadManager.Prototype.csproj -c Release
dotnet build tests/AiDownloadManager.Engine.Tests/AiDownloadManager.Engine.Tests.csproj -c Release
dotnet build tests/AiDownloadManager.TestRunner/AiDownloadManager.TestRunner.csproj -c Release
dotnet run --project tests/AiDownloadManager.TestRunner -c Release --no-build
echo "BUILD + TESTS OK (WPF presentation project excluded: Windows-only)"
