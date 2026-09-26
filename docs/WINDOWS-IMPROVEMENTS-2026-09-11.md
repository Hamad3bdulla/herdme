# Windows Improvement Delivery

Scope: implement the seven requested Windows improvements on the existing application.

- [x] Guided service installation with version and preflight checks.
- [x] Shared PHP, Node, and service download history, cancellation, retry, and supported resume.
- [x] Actionable diagnostics and repair for runtime, extension, port, and certificate failures.
- [x] Arabic error presentation and readable mixed-direction paths and numbers.
- [x] Service data backups before updates, with runtime rollback separated from data recovery.
- [x] Site favorites, responsive search, status, and last error.
- [x] Upgrade preservation tests for projects, settings, and service data.

Verification passed: focused contract tests, native WinUI compilation, formatting,
PowerShell parsing, installer upgrade acceptance, and English/Arabic UI evidence.
Public signing remains dependent on the owner's Windows signing certificate.
