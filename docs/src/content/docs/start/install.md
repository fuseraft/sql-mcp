---
title: Install
description: Build sql-mcp from source, publish a self-contained binary, or build the Docker image.
---

sql-mcp is a .NET 10 program. There are no prebuilt releases yet: you build it from source, either
to run with `dotnet run`, as a self-contained binary that needs no .NET on the target machine, or as
a Docker image.

## From source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```sh
git clone https://github.com/fuseraft/sql-mcp.git
cd sql-mcp
dotnet restore
dotnet run              # Streamable HTTP on http://localhost:8080/mcp
dotnet run -- --stdio   # stdio
```

`dotnet run` is fine for trying it out. For a client to launch, or for a server, publish a binary.

## Self-contained binary

```sh
dotnet publish -p:PublishProfile=linux-x64   # publish/linux-x64/sql-mcp
dotnet publish -p:PublishProfile=win-x64     # publish/win-x64/sql-mcp.exe
```

The output folder has everything sql-mcp needs, with no .NET runtime required on the target
machine. Copy the whole folder, for example to `/opt/sql-mcp` or `C:\sql-mcp`.

`appsettings.json` is read from the folder that contains the executable, not from the current
directory. That's what lets an MCP client launch `/opt/sql-mcp/sql-mcp --stdio` from anywhere and
still get its configuration.

## Docker

```sh
docker build -t sql-mcp .
```

The image runs Streamable HTTP on port 8080 as the base image's unprivileged `app` user (uid
1654). Because it listens on all interfaces inside the container, it won't start without at least
one agent token. [Deployment](/sql-mcp/guides/deployment/#docker) shows how to run it.

## Next

[Quickstart](/sql-mcp/start/quickstart/) configures a database, creates a read-only login for it and
connects a client.
