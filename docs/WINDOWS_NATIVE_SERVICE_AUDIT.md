# Windows Native Service Audit

Audit date: 2026-07-25

HerdMe requires native Windows x64 service processes. Docker, WSL, Cygwin,
closed-source ports, and unverified third-party binaries do not satisfy this
requirement. A service remains visible but disabled when upstream does not
provide a suitable package or a reproducible source build.

## Valkey

Status: unavailable for supported native Windows installation.

- The official Valkey 9.1.1 release has no attached binaries:
  <https://github.com/valkey-io/valkey/releases/tag/9.1.1>
- The official build documentation lists Linux, macOS, OpenBSD, NetBSD, and
  FreeBSD, but not Windows:
  <https://github.com/valkey-io/valkey/blob/9.1.1/README.md>
- Upstream issue 92 records that the server is POSIX-oriented and that upstream
  has no current plan to publish Windows binaries:
  <https://github.com/valkey-io/valkey/issues/92>
- Pull request 3427 is not an accepted release path. At the audit date it is
  unmerged and conflicted, has no successful Windows check, bypasses the unit
  tests, and fetches an unpinned compatibility layer from a contributor fork:
  <https://github.com/valkey-io/valkey/pull/3427>

The experimental pull request also leaves important persistence and native x64
validation questions unresolved. HerdMe must not package it as Valkey 9.1.1.

## Typesense

Status: unavailable for supported native Windows installation.

- The official Typesense 30.2 release has no attached Windows asset:
  <https://github.com/typesense/typesense/releases/tag/v30.2>
- The official README offers binaries for Linux x64/arm64 and macOS x64, or an
  official Docker image, but no Windows executable:
  <https://github.com/typesense/typesense/blob/v30.2/README.md>
- The v30.2 release script publishes only Linux and macOS archives:
  <https://github.com/typesense/typesense/blob/v30.2/publish_release.sh>
- Upstream issue 1371 states that native Windows support requires significant
  changes and is not currently planned:
  <https://github.com/typesense/typesense/issues/1371>
- Upstream issues 722 and 1001 confirm that Linux-specific code and dependencies
  need porting before a native executable can be built:
  <https://github.com/typesense/typesense/issues/722>
  <https://github.com/typesense/typesense/issues/1001>

The v30.2 sources directly use POSIX system metrics, sockets, dynamic loading,
signals, and OS-specific Bazel dependency selections. A renamed Linux binary or
WSL launcher is not a native Windows build.

## Enablement Gates

Valkey or Typesense can become installable only after all of these checks pass:

1. Build from an immutable upstream release tag or an audited HerdMe patch set
   whose complete source remains available under a compatible open-source license.
2. Pin every source archive, patch, toolchain, and dependency by version and
   SHA-256. Floating branches and unpinned compatibility repositories are forbidden.
3. Build a native PE32+ x86-64 executable on the Windows CI runner without WSL,
   Docker, Cygwin, or an interactive terminal.
4. Run upstream unit and integration tests plus HerdMe lifecycle tests for
   installation, loopback-only binding, start, readiness, stop, restart,
   persistence, automatic startup, and clean removal.
5. Verify the exact packaged archive checksum and publish its source commit,
   dependency manifest, build log, license, and third-party notices.
6. Run the packaged service on a clean Windows x64 machine before changing
   `IsInstallable` to `true` in `ManagedServiceCatalog`.

Until every gate passes, the disabled catalog entries are intentional and must
not be replaced by a similarly named service or an undocumented compatibility
layer.
