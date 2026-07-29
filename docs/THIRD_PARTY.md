# Third-party software

HerdMe is MIT-licensed and has no activation, subscription, license key, or
paid feature gate. It downloads optional open-source development tools from
their upstream or explicitly documented Windows distribution endpoints. Those tools are separate works and
retain their upstream licenses.

| Tool | Upstream license | Official project |
| --- | --- | --- |
| PHP | PHP License 3.01 | https://www.php.net/license/3_01.txt |
| Xdebug | PHP License 3.01 | https://xdebug.org/docs/license |
| Git for Windows (MinGit) | GPL-2.0 | https://github.com/git-for-windows/git |
| Composer | MIT | https://github.com/composer/composer |
| Laravel Installer | MIT | https://github.com/laravel/installer |
| Node.js | MIT | https://github.com/nodejs/node |
| MariaDB Server | GPL-2.0 | https://github.com/MariaDB/server |
| MySQL Community Server | GPL-2.0 | https://github.com/mysql/mysql-server |
| PostgreSQL | PostgreSQL License | https://www.postgresql.org/about/licence/ |
| MongoDB Community Server | SSPL-1.0 | https://github.com/mongodb/mongo |
| Redis | AGPL-3.0, RSALv2, or SSPLv1 | https://github.com/redis/redis |
| Redis for Windows port | Apache-2.0 | https://github.com/redis-windows/redis-windows |
| Valkey | BSD-3-Clause | https://github.com/valkey-io/valkey |
| Meilisearch Community Edition | MIT | https://github.com/meilisearch/meilisearch |
| Typesense | GPL-3.0 | https://github.com/typesense/typesense |
| MinIO Server | AGPL-3.0 | https://github.com/minio/minio |
| RustFS | Apache-2.0 | https://github.com/rustfs/rustfs |
| H.NotifyIcon | MIT | https://github.com/HavenDV/H.NotifyIcon |
| Windows App SDK | MIT | https://github.com/microsoft/WindowsAppSDK |
| Microsoft Visual C++ Runtime | Microsoft Software License Terms | https://visualstudio.microsoft.com/license-terms/vs2022-cruntime/ |

Downloaded binaries are stored only in HerdMe-owned application data and are
not copied into source distributions or relicensed under HerdMe's MIT license.
On macOS, PHP and managed services are installed from Homebrew formulae. HerdMe
accepts only its documented formula and tap names, validates the resolved
formula identity, and delegates repository, formula metadata, bottle download,
and bottle checksum verification to the installed Homebrew client. HerdMe does
not independently pin a Homebrew repository commit or bottle digest, so this
trust model is intentionally different from its direct archive downloads.
Node.js archives are
downloaded from the official Node.js release directory and must match the exact
filename and SHA-256 digest published in that release's `SHASUMS256.txt` before
they are unpacked. The Windows MinGit archive is selected from the official Git
for Windows GitHub release and must match the asset's published size and
SHA-256 digest before it is unpacked. The Composer installer must match Composer's separately
published SHA-384 signature before it is executed.
Xdebug source archives for macOS are downloaded from `xdebug.org` only after
HerdMe extracts the exact release filename and SHA-256 digest published on the
official download page. The archive digest and every archive path are checked
before extraction and compilation.

The Windows build copies the signed x64 Microsoft VC143 app-local runtime from
the licensed Visual Studio C++ build tools into the HerdMe package. HerdMe then
copies those runtime files beside each managed PHP executable so PHP works on a
clean Windows installation without a system-wide Visual C++ installation.

Windows service packages require their published SHA-256 digest when available.
The MySQL Windows ZIP is the explicit exception: Oracle currently publishes MD5
and a detached signature, so HerdMe pins the exact filename, release, official
HTTPS CDN path, and Oracle-published MD5 value.
The EDB PostgreSQL Windows archive does not expose a machine-readable checksum;
HerdMe therefore pins the exact release URL and a full SHA-256 calculated and
verified during HerdMe release preparation.
