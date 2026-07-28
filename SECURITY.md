# Security policy

## Supported versions

Security fixes are applied to the latest release and the default branch. Older
development builds may be asked to upgrade before a report can be reproduced.

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability. Use GitHub's private
[security advisory form](https://github.com/Hamad3bdulla/herdme/security/advisories/new)
and include:

- the affected HerdMe version and operating system;
- clear reproduction steps or a minimal proof of concept;
- the expected impact and any known workarounds;
- whether the report involves a third-party package downloaded by HerdMe.

Reports about privileged helpers, local HTTP/DNS/SMTP listeners, certificate
handling, path traversal, update metadata, or downloaded executable integrity
are especially useful. Please allow the project time to investigate and publish
a fix before sharing the issue publicly.

HerdMe will acknowledge a complete report as soon as practical and will keep
the reporter informed when the impact, fix, and disclosure timeline are known.

## Scope

HerdMe listens only on loopback interfaces and manages development tools in its
own application-data directory. A report should demonstrate a boundary bypass,
privilege escalation, unintended network exposure, arbitrary file access, or a
supply-chain integrity failure. Vulnerabilities in an upstream tool should also
be reported to that tool's maintainers.
