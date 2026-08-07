#!/usr/bin/env node
"use strict";

// Launcher for the `repoctx` command installed from npm.
//
// It resolves the platform-specific binary and hands the process over to it
// with the stdio streams inherited, so `repoctx mcp` keeps working: the MCP
// stdio transport speaks raw JSON-RPC over stdin/stdout and must not be
// buffered, wrapped or line-translated by this shim.

const { chmodSync, constants, accessSync } = require("node:fs");
const { spawnSync } = require("node:child_process");
const { createResolver, resolveBinary } = require("../lib/platform.js");

function main() {
  const resolution = resolveBinary({
    process,
    resolve: createResolver([__dirname, process.cwd()], require.resolve),
  });

  if (!resolution.ok) {
    process.stderr.write(`repoctx: ${resolution.message}\n`);
    return 1;
  }

  ensureExecutable(resolution.binary);

  const result = spawnSync(resolution.binary, process.argv.slice(2), {
    stdio: "inherit",
    windowsHide: true,
  });

  if (result.error) {
    process.stderr.write(
      `repoctx: could not start ${resolution.binary}: ${result.error.message}\n`,
    );
    return 1;
  }

  // A child killed by a signal has no exit status. Report it the way a shell
  // does so callers can still distinguish it from an ordinary failure.
  if (result.signal) {
    process.stderr.write(`repoctx: terminated by signal ${result.signal}\n`);
    return 1;
  }

  return result.status === null ? 1 : result.status;
}

/**
 * npm preserves file modes from the package tarball, but some registries,
 * mirrors and extraction paths do not. Restoring the execute bit here is
 * cheaper than a postinstall script and keeps the platform packages free of
 * lifecycle scripts, which many organizations block outright.
 */
function ensureExecutable(binary) {
  if (process.platform === "win32") {
    return;
  }

  try {
    accessSync(binary, constants.X_OK);
  } catch {
    try {
      chmodSync(binary, 0o755);
    } catch {
      // Read-only install (a global store, a container layer): let the spawn
      // fail with its own, more precise message.
    }
  }
}

process.exitCode = main();
