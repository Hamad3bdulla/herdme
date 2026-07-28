# Contributing to HerdMe

HerdMe is an independent, open-source local development environment. Bug fixes,
tests, documentation, accessibility improvements, and carefully scoped platform
features are welcome.

By participating, you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).
Contributions are provided under the repository's [MIT license](LICENSE).

## Before opening an issue

- Report suspected vulnerabilities privately as described in
  [SECURITY.md](SECURITY.md). Never include secrets, private keys, credentials,
  or private project source in a public issue.
- Search existing issues before creating a duplicate.
- Reproduce bugs with the latest default-branch source or latest release when
  practical.
- Keep HerdMe independent. Do not submit code, assets, binaries, configuration,
  screenshots, signing material, or private implementation details copied from
  another application.

Expose, extra shortcuts, third-party integrations, and Forge integration are
outside the current product scope. Discuss large changes before implementing
them so platform behavior, security boundaries, and acceptance evidence are
clear.

## Development setup

The macOS requirements and build commands are in [README.md](README.md). The
native Windows requirements and commands are in
[Windows/README.md](Windows/README.md). Generate `HerdMe.xcodeproj` with
XcodeGen; the generated project and all build output are intentionally ignored.

Create a focused branch and keep unrelated formatting or generated-file changes
out of the pull request. Do not commit downloaded runtimes, certificates,
credentials, update private keys, logs, site data, or build artifacts.

## Required validation

Run the checks that cover every platform and behavior changed by the pull
request. At minimum, documentation-only changes must pass `git diff --check`.

Run the repository formatting gates before platform builds:

```sh
xcrun swift-format lint --strict --recursive --parallel HerdMe HerdMeTests HerdMeUITests
find Core Tools -type f \( -name '*.c' -o -name '*.cc' -o -name '*.cpp' -o -name '*.h' -o -name '*.hpp' \) \
  -print0 | xargs -0 xcrun clang-format --dry-run --Werror
dotnet format Windows/HerdMe.Windows/HerdMe.Windows.csproj --verify-no-changes
```

The full C# formatting command targets WinUI and must run on Windows. The
cross-platform contract project can be used for a narrower service/model check
on macOS or Linux.

For macOS application changes:

```sh
xcodegen generate
xcodebuild \
  -project HerdMe.xcodeproj \
  -scheme HerdMe \
  -configuration Debug \
  -destination 'platform=macOS' \
  -derivedDataPath build/contributor-tests \
  test
```

For portable Core changes:

```sh
cmake -S Core -B build/core -DBUILD_TESTING=ON
cmake --build build/core
ctest --test-dir build/core --output-on-failure
```

For Windows changes, run from PowerShell on Windows x64:

```powershell
.\Windows\build.ps1 -Architecture x64 -Configuration Debug
```

Changes to native Windows behavior must also complete the relevant items in
[Windows/ACCEPTANCE.md](Windows/ACCEPTANCE.md) on real Windows hardware. A
cross-platform C# contract run does not replace the native WinUI build or
hardware acceptance.

Changes to release metadata, update handling, packaging, or workflows must also
run the version, update-manifest, and release-asset contracts documented in
[docs/RELEASING.md](docs/RELEASING.md).

## Pull requests

A pull request should explain the user-visible outcome, the platforms affected,
the security or migration impact, and the exact validation performed. Add or
update tests for behavioral changes. Update `CHANGELOG.md` for user-visible
changes and keep `docs/PARITY.md` honest about anything that still requires
native, hosted, signed, or clean-machine evidence.

Reviewers may ask for a smaller change when unrelated work makes risk or
verification difficult to evaluate. A change is not publication-ready merely
because it compiles on one platform.
