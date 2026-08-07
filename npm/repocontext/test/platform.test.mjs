// Unit tests for the platform resolution used by the npm launcher.
// Run with: node --test npm/repocontext/test/
import assert from "node:assert/strict";
import path from "node:path";
import { test } from "node:test";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const { createResolver, resolveBinary, targetFor, targets, PAYLOAD_DIRECTORY } =
  require("../lib/platform.js");

/**
 * A process stand-in. `header` mimics a glibc runtime by default; musl reports
 * omit `glibcVersionRuntime` entirely, which `{ header: {} }` reproduces.
 */
function fakeProcess(platform, arch, header = { glibcVersionRuntime: "2.39" }) {
  return {
    platform,
    arch,
    report: { getReport: () => ({ header }) },
  };
}

/** A resolver that pretends `packageName` is installed under /store. */
function fakeResolver(packageName) {
  return (request) => {
    if (request === `${packageName}/package.json`) {
      return path.join("/store", packageName, "package.json");
    }

    throw new Error(`Cannot find module '${request}'`);
  };
}

test("every supported target is unique in package, rid and key", () => {
  const all = targets();
  assert.equal(all.length, 6);
  for (const field of ["key", "package", "rid"]) {
    const values = all.map((t) => t[field]);
    assert.equal(new Set(values).size, all.length, `duplicate ${field}`);
  }
});

test("windows targets carry the .exe suffix, unix targets do not", () => {
  for (const target of targets()) {
    const expected = target.key.startsWith("win32-") ? "repoctx.exe" : "repoctx";
    assert.equal(target.binary, expected, target.key);
  }
});

test("targetFor maps the six supported platform/arch pairs", () => {
  assert.equal(targetFor("linux", "x64").package, "repocontext-linux-x64");
  assert.equal(targetFor("linux", "arm64").rid, "linux-arm64");
  assert.equal(targetFor("darwin", "x64").rid, "osx-x64");
  assert.equal(targetFor("darwin", "arm64").rid, "osx-arm64");
  assert.equal(targetFor("win32", "x64").rid, "win-x64");
  assert.equal(targetFor("win32", "arm64").rid, "win-arm64");
});

test("targetFor returns null for an unsupported pair", () => {
  assert.equal(targetFor("freebsd", "x64"), null);
  assert.equal(targetFor("linux", "s390x"), null);
});

test("resolveBinary points at the payload inside the platform package", () => {
  const resolution = resolveBinary({
    process: fakeProcess("linux", "x64"),
    resolve: fakeResolver("repocontext-linux-x64"),
  });

  assert.equal(resolution.ok, true);
  assert.equal(
    resolution.binary,
    path.join("/store", "repocontext-linux-x64", PAYLOAD_DIRECTORY, "repoctx"),
  );
});

test("resolveBinary appends .exe on Windows", () => {
  const resolution = resolveBinary({
    process: fakeProcess("win32", "arm64"),
    resolve: fakeResolver("repocontext-win32-arm64"),
  });

  assert.equal(resolution.ok, true);
  assert.equal(path.basename(resolution.binary), "repoctx.exe");
});

test("an unsupported platform names the supported ones and the dotnet fallback", () => {
  const resolution = resolveBinary({
    process: fakeProcess("sunos", "x64"),
    resolve: fakeResolver("repocontext-linux-x64"),
  });

  assert.equal(resolution.ok, false);
  assert.equal(resolution.reason, "unsupported-platform");
  assert.match(resolution.message, /linux-x64/);
  assert.match(resolution.message, /dotnet tool install/);
});

test("musl linux is refused with an actionable message, not a loader crash", () => {
  const resolution = resolveBinary({
    process: fakeProcess("linux", "x64", {}),
    resolve: fakeResolver("repocontext-linux-x64"),
  });

  assert.equal(resolution.ok, false);
  assert.equal(resolution.reason, "musl");
  assert.match(resolution.message, /Alpine/);
});

test("glibc linux is not mistaken for musl when the report is unavailable", () => {
  const brokenReport = {
    platform: "linux",
    arch: "x64",
    report: {
      getReport() {
        throw new Error("report unavailable");
      },
    },
  };

  const resolution = resolveBinary({
    process: brokenReport,
    resolve: fakeResolver("repocontext-linux-x64"),
  });

  assert.equal(resolution.ok, true);
});

test("createResolver falls back to the next starting directory", () => {
  const attempted = [];
  const resolve = createResolver(["/link/bin", "/consumer"], (request, { paths }) => {
    attempted.push(paths[0]);
    if (paths[0] !== "/consumer") {
      throw new Error(`Cannot find module '${request}'`);
    }

    return path.join("/consumer", "node_modules", "repocontext-linux-x64", "package.json");
  });

  const resolution = resolveBinary({ process: fakeProcess("linux", "x64"), resolve });

  assert.equal(resolution.ok, true);
  assert.deepEqual(attempted, ["/link/bin", "/consumer"]);
});

test("createResolver rethrows when no starting directory has the package", () => {
  const resolve = createResolver(["/a", "/b"], (request) => {
    throw new Error(`Cannot find module '${request}'`);
  });

  assert.throws(() => resolve("repocontext-linux-x64/package.json"), /Cannot find module/);
});

test("a skipped optional dependency explains how to recover", () => {
  const resolution = resolveBinary({
    process: fakeProcess("darwin", "arm64"),
    resolve: fakeResolver("repocontext-linux-x64"),
  });

  assert.equal(resolution.ok, false);
  assert.equal(resolution.reason, "missing-package");
  assert.match(resolution.message, /repocontext-darwin-arm64/);
  assert.match(resolution.message, /optional/);
});
