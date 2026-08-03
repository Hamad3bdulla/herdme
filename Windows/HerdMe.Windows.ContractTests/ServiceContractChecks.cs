using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using HerdMe.Windows.Models;
using HerdMe.Windows.Services;

internal static partial class ContractChecks
{
    internal static async Task VerifyServiceContractsAsync(string supportRoot)
    {
        var queueArguments = new SiteQueueWorkerOptions(
            "redis", "high,default", 3, 120, 2, 500, 3_600
        ).Arguments();
        Check(
            queueArguments.SequenceEqual([
                "queue:work",
                "redis",
                "--no-interaction",
                "--queue=high,default",
                "--tries=3",
                "--timeout=120",
                "--sleep=2",
                "--max-jobs=500",
                "--max-time=3600"
            ]),
            "advanced queue settings produce bounded Artisan arguments"
        );
        Throws<ArgumentException>(
            () => new SiteQueueWorkerOptions("redis --force").Arguments(),
            "queue connection names cannot inject command arguments"
        );
        Check(
            QueueManagementService.ValidJobId("550e8400-e29b-41d4-a716-446655440000")
                && QueueManagementService.ValidJobId("42")
                && !QueueManagementService.ValidJobId("42 --force"),
            "failed queue job identifiers are validated before Artisan execution"
        );
        var databaseInspection = DatabaseConnectionInspector.ParseSuccessful(
            "mariadb", ["11.8.2-MariaDB", "17", "1048576", "1"],
            TimeSpan.FromMilliseconds(24)
        );
        Check(
            databaseInspection.Connected && databaseInspection.TableCount == 17
                && databaseInspection.SizeBytes == 1_048_576
                && databaseInspection.ServerVersion == "11.8.2-MariaDB",
            "database connection inspection parses read-only server metrics"
        );
        Check(
            ComposerCommandRunner.Presets.Select(preset => preset.Id).SequenceEqual(
                new[] { "install", "update", "validate", "audit", "dump-autoload" }
            ),
            "site Composer commands expose a bounded, predictable preset list"
        );
        Check(
            ComposerCommandRunner.RequireArguments("vendor/package").SequenceEqual(
                new[] { "require", "vendor/package", "--no-interaction" }
            ),
            "site Composer package installation uses a structured argument list"
        );
        Throws<ArgumentException>(
            () => ComposerCommandRunner.RequireArguments("--working-dir elsewhere"),
            "site Composer package input cannot inject command options"
        );
        Check(
            SiteDatabaseProvisioner.IsSupportedImportFile("site.sql")
                && SiteDatabaseProvisioner.IsSupportedImportFile("site.SQL.GZ")
                && !SiteDatabaseProvisioner.IsSupportedImportFile("site.dump"),
            "site database imports accept plain or gzip-compressed SQL only"
        );
        var mergedPerformance = WindowsLocalEnvironment.MergePerformance(
            new SitePerformanceSnapshot(2, 1, 1, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), DateTimeOffset.UtcNow, []),
            new SitePerformanceSnapshot(1, 0, 0, TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(40), DateTimeOffset.UtcNow.AddSeconds(-1), [])
        );
        Check(
            mergedPerformance.RequestCount == 3
                && mergedPerformance.ServerErrorCount == 1
                && mergedPerformance.AverageDuration == TimeSpan.FromMilliseconds(20),
            "site performance snapshots merge HTTP and HTTPS counters correctly"
        );
        Check(
            SiteHealthInspector.EnvironmentValue("APP_KEY=base64:test\nDB_DATABASE=herdme", "APP_KEY") == "base64:test",
            "site health reads Laravel environment values safely"
        );
        await using (var siteProcesses = new SiteProcessManager())
        {
            var processState = siteProcesses.State(
                Path.Combine(supportRoot, "not-running"),
                SiteBackgroundProcessKind.Queue
            );
            Check(
                !processState.Running && processState.ExitCode is null,
                "site background processes begin in an explicit stopped state"
            );
        }
        Check(
            new ManagedServiceInstance().StartAutomatically,
            "new managed service instances start automatically"
        );
        Check(ManagedServiceCatalog.Get("mongodb").DefaultPort == 27_017, "Windows service catalog includes MongoDB");
        Check(ManagedServiceCatalog.Get("mysql").DefaultPort == 3_306, "Windows service catalog includes MySQL");
        Check(ManagedServiceCatalog.Get("postgresql").DefaultPort == 5_432, "Windows service catalog includes PostgreSQL");
        Check(ManagedServiceCatalog.Get("redis").DefaultPort == 6_379, "Windows service catalog includes Redis");
        Check(ManagedServiceCatalog.Get("rustfs").DefaultPort == 9_000, "Windows service catalog includes RustFS");
        var tablePlusSite = TablePlusConnection.UriForDatabase(
            new ManagedServiceInstance
            {
                DefinitionId = "postgresql",
                Name = "PostgreSQL",
                Port = 5_432
            },
            new SiteDatabaseProvisioning("example", "herdme_example", "secret123")
        );
        Check(
            tablePlusSite is not null
                && tablePlusSite.Scheme == "postgresql"
                && tablePlusSite.AbsolutePath == "/example"
                && tablePlusSite.UserInfo.StartsWith("herdme_example:", StringComparison.Ordinal),
            "site databases open TablePlus with their dedicated database and credentials"
        );
        var valkeyDefinition = ManagedServiceCatalog.Get("valkey");
        Check(valkeyDefinition.DefaultPort == 6_379, "Windows service catalog includes Valkey");
        Check(!valkeyDefinition.IsInstallable, "Valkey is disabled without an official native Windows package");
        Check(
            !string.IsNullOrWhiteSpace(valkeyDefinition.UnavailableReason),
            "Valkey explains why native Windows installation is unavailable"
        );
        var typesenseDefinition = ManagedServiceCatalog.Get("typesense");
        Check(typesenseDefinition.DefaultPort == 8_108, "Windows service catalog includes Typesense");
        Check(!typesenseDefinition.IsInstallable, "Typesense is disabled without an official native Windows package");
        Check(
            !string.IsNullOrWhiteSpace(typesenseDefinition.UnavailableReason),
            "Typesense explains why native Windows installation is unavailable"
        );
        Check(
            WindowsServiceManager.AvailablePort(
                3_306,
                new HashSet<int> { 3_306, 3_307 },
                port => port != 3_308
            ) == 3_309,
            "service port selection skips configured and externally unavailable ports"
        );
        Check(
            WindowsServiceManager.AvailablePort(
                65_535,
                new HashSet<int> { 65_535, 1_024 },
                _ => true
            ) == 1_025,
            "service port selection wraps safely after the final TCP port"
        );
        var externalServiceListener = new TcpListener(IPAddress.Loopback, 0);
        externalServiceListener.Start();
        try
        {
            var externalPort = ((IPEndPoint)externalServiceListener.LocalEndpoint).Port;
            var suggestedPort = WindowsServiceManager.AvailablePort(externalPort);
            Check(
                suggestedPort is not null
                    && suggestedPort != externalPort
                    && WindowsServiceManager.IsPortAvailable(suggestedPort.Value),
                "service port selection proposes a free loopback port after an external conflict"
            );
            using var externalProbe = new TcpClient();
            await externalProbe.ConnectAsync(IPAddress.Loopback, externalPort)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Check(
                externalProbe.Connected,
                "service port recovery never terminates the external port owner"
            );
        }
        finally
        {
            externalServiceListener.Stop();
        }

        var environmentProject = Path.Combine(supportRoot, "environment-project");
        Directory.CreateDirectory(environmentProject);
        await File.WriteAllTextAsync(
            Path.Combine(environmentProject, ".env.example"),
            "APP_NAME=Example\r\n# Keep this comment\r\nDB_HOST=localhost\r\n"
        );
        var environmentInstance = new ManagedServiceInstance
        {
            DefinitionId = "mysql",
            Name = "Local MySQL",
            Port = 3_306
        };
        var environmentCredentials = new ServiceCredentials(
            "herdme_testuser",
            "test_secret_0123456789_ABCDEFGHIJKLMNOP"
        );
        var firstEnvironmentUpdate = ServiceEnvironmentFile.Update(
            environmentProject,
            environmentInstance,
            environmentCredentials
        );
        Check(firstEnvironmentUpdate.CreatedFile, "service .env updates start from .env.example");
        Check(
            firstEnvironmentUpdate.UpdatedKeys == 1 && firstEnvironmentUpdate.AddedKeys == 5,
            "service .env updates report replaced and appended variables"
        );
        environmentInstance.Port = 3_307;
        var secondEnvironmentUpdate = ServiceEnvironmentFile.Update(
            environmentProject,
            environmentInstance,
            environmentCredentials
        );
        var environmentContents = await File.ReadAllTextAsync(
            Path.Combine(environmentProject, ".env")
        );
        Check(
            !secondEnvironmentUpdate.CreatedFile
                && secondEnvironmentUpdate.UpdatedKeys == 6
                && secondEnvironmentUpdate.AddedKeys == 0,
            "service .env updates replace existing variables without appending duplicates"
        );
        Check(
            environmentContents.Contains("# Keep this comment\r\n", StringComparison.Ordinal)
                && environmentContents.Contains("DB_PORT=3307\r\n", StringComparison.Ordinal),
            "service .env updates preserve CRLF and unrelated comments"
        );
        Check(
            environmentContents.Split("\r\n").Count(line => line.StartsWith("DB_HOST=", StringComparison.Ordinal)) == 1,
            "service .env updates keep one effective assignment per managed key"
        );
        Check(
            SiteDatabaseProvisioner.SuggestedDatabaseName("  24 Demo Store!  ") == "site_24_demo_store",
            "site database names are derived from display names as portable SQL identifiers"
        );
        Check(
            SiteDatabaseProvisioner.IsValidDatabaseName("demo_store_2")
                && !SiteDatabaseProvisioner.IsValidDatabaseName("2-demo"),
            "site database names reject unsafe SQL identifiers"
        );
        var generatedDatabase = SiteDatabaseProvisioner.Generate("demo_store");
        Check(
            generatedDatabase.Username.StartsWith("herdme_", StringComparison.Ordinal)
                && generatedDatabase.Username.Length == 23
                && generatedDatabase.Password.Length == 48
                && generatedDatabase.Password.All(char.IsAsciiHexDigit),
            "site database credentials are generated in a portable restricted alphabet"
        );
        var siteDatabase = new SiteDatabaseProvisioning(
            "demo_store",
            "herdme_0123456789abcdef",
            "0123456789abcdef0123456789abcdef0123456789abcdef"
        );
        var mysqlSiteSql = SiteDatabaseProvisioner.MySqlCreateDatabaseSql(siteDatabase);
        Check(
            mysqlSiteSql.Contains("CREATE DATABASE `demo_store`", StringComparison.Ordinal)
                && mysqlSiteSql.Contains(
                    "GRANT ALL PRIVILEGES ON `demo_store`.*",
                    StringComparison.Ordinal
                )
                && !mysqlSiteSql.Contains("ON *.*", StringComparison.Ordinal),
            "site MySQL users are limited to their own database"
        );
        var postgreSqlSiteSql = SiteDatabaseProvisioner.PostgreSqlCreateDatabaseSql(siteDatabase);
        Check(
            postgreSqlSiteSql.CreateRole.Contains(
                "CREATE ROLE \"herdme_0123456789abcdef\" WITH LOGIN",
                StringComparison.Ordinal
            )
                && postgreSqlSiteSql.CreateDatabase.Contains(
                    "CREATE DATABASE \"demo_store\" OWNER \"herdme_0123456789abcdef\"",
                    StringComparison.Ordinal
                ),
            "site PostgreSQL databases are owned by a dedicated login role"
        );
        var siteDatabaseProject = Path.Combine(supportRoot, "site-database-environment");
        Directory.CreateDirectory(siteDatabaseProject);
        var siteDatabaseUpdate = ServiceEnvironmentFile.Update(
            siteDatabaseProject,
            ServiceEnvironmentConfiguration.DatabaseVariables(environmentInstance, siteDatabase),
            "Local MySQL database demo_store"
        );
        var siteDatabaseEnvironment = await File.ReadAllTextAsync(
            Path.Combine(siteDatabaseProject, ".env")
        );
        Check(
            siteDatabaseUpdate.AddedKeys == 6
                && siteDatabaseEnvironment.Contains("DB_DATABASE=demo_store", StringComparison.Ordinal)
                && siteDatabaseEnvironment.Contains(
                    "DB_USERNAME=herdme_0123456789abcdef",
                    StringComparison.Ordinal
                )
                && siteDatabaseEnvironment.Contains(
                    "DB_PASSWORD=0123456789abcdef0123456789abcdef0123456789abcdef",
                    StringComparison.Ordinal
                ),
            "site database credentials can be added to a project's .env"
        );
        var mailEnvironmentProject = Path.Combine(supportRoot, "mail-environment-project");
        Directory.CreateDirectory(mailEnvironmentProject);
        await File.WriteAllTextAsync(
            Path.Combine(mailEnvironmentProject, ".env.example"),
            "APP_NAME=MailExample\nMAIL_HOST=localhost\n"
        );
        var firstMailEnvironmentUpdate = ServiceEnvironmentFile.Update(
            mailEnvironmentProject,
            MailEnvironmentConfiguration.Variables(2_525),
            "HerdMe Mail"
        );
        var secondMailEnvironmentUpdate = ServiceEnvironmentFile.Update(
            mailEnvironmentProject,
            MailEnvironmentConfiguration.Variables(2_526),
            "HerdMe Mail"
        );
        var mailEnvironmentContents = await File.ReadAllTextAsync(
            Path.Combine(mailEnvironmentProject, ".env")
        );
        Check(
            firstMailEnvironmentUpdate.CreatedFile
                && firstMailEnvironmentUpdate.AddedKeys == 2
                && firstMailEnvironmentUpdate.UpdatedKeys == 1,
            "mail .env updates create a site file from .env.example"
        );
        Check(
            !secondMailEnvironmentUpdate.CreatedFile
                && secondMailEnvironmentUpdate.AddedKeys == 0
                && secondMailEnvironmentUpdate.UpdatedKeys == 3
                && mailEnvironmentContents.Contains("MAIL_MAILER=smtp\n", StringComparison.Ordinal)
                && mailEnvironmentContents.Contains("MAIL_HOST=127.0.0.1\n", StringComparison.Ordinal)
                && mailEnvironmentContents.Contains("MAIL_PORT=2526\n", StringComparison.Ordinal)
                && mailEnvironmentContents.Split('\n').Count(line =>
                    line.StartsWith("MAIL_PORT=", StringComparison.Ordinal)
                ) == 1,
            "mail .env updates write the active SMTP port without duplicating variables"
        );

        var serviceConfigurationRoot = Path.Combine(supportRoot, "stable-service-configuration");
        var serviceConfigurationManager = new WindowsServiceManager(serviceConfigurationRoot);
        var persistedInstance = new ManagedServiceInstance
        {
            DefinitionId = "mysql",
            Name = "Persistent MySQL",
            Port = 3_308
        };
        Directory.CreateDirectory(Path.GetDirectoryName(serviceConfigurationManager.ConfigurationPath)!);
        await File.WriteAllTextAsync(
            serviceConfigurationManager.ConfigurationPath,
            JsonSerializer.Serialize(new[] { persistedInstance })
        );
        Check(
            serviceConfigurationManager.LoadInstances().Single().Id == persistedInstance.Id,
            "service settings load an initial persisted instance"
        );
        using (var configurationLock = new FileStream(
            serviceConfigurationManager.ConfigurationPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None
        ))
        {
            var retainedInstances = serviceConfigurationManager.LoadInstances();
            Check(
                retainedInstances.Count == 1
                    && retainedInstances[0].Id == persistedInstance.Id
                    && File.Exists(serviceConfigurationManager.ConfigurationPath),
                "a transient service settings lock keeps the last known list and original file"
            );
        }
        Check(
            serviceConfigurationManager.LoadInstances().Single().Id == persistedInstance.Id,
            "service settings recover after a transient read conflict"
        );

        var installStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishInstall = new TaskCompletionSource<ServicePackageRelease>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var operationManager = new WindowsServiceManager(
            Path.Combine(supportRoot, "shared-service-install"),
            installPackage: async (definitionId, cancellationToken) =>
            {
                installStarted.TrySetResult();
                return await finishInstall.Task.WaitAsync(cancellationToken);
            }
        );
        var operationChanges = 0;
        operationManager.Changed += (_, _) => Interlocked.Increment(ref operationChanges);
        var firstInstall = operationManager.InstallAsync("mysql");
        await installStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondInstall = operationManager.InstallAsync("mysql");
        Check(
            ReferenceEquals(firstInstall, secondInstall)
                && operationManager.IsInstalling("mysql")
                && operationManager.State(Guid.NewGuid(), "mysql") == ManagedServiceState.Installing,
            "service installation remains shared and visible while pages change"
        );
        finishInstall.SetResult(new ServicePackageRelease(
            "mysql",
            "9.9.9",
            "mysql.zip",
            ServicePackageChecksumAlgorithm.Sha256,
            new string('0', 64),
            new Uri("https://example.test/mysql.zip"),
            true
        ));
        await Task.WhenAll(firstInstall, secondInstall).WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Yield();
        Check(
            !operationManager.IsInstalling("mysql") && operationChanges >= 2,
            "service installation publishes its completed state to a returning page"
        );
        var editorProject = Path.Combine(supportRoot, "environment-editor-project");
        Directory.CreateDirectory(editorProject);
        await File.WriteAllTextAsync(
            Path.Combine(editorProject, ".env.example"),
            "APP_NAME=Example\nAPP_ENV=local\n"
        );
        var environmentDraft = ProjectEnvironmentFile.Load(editorProject);
        Check(
            !environmentDraft.Exists
                && environmentDraft.LoadedFromExample
                && environmentDraft.Revision == ProjectEnvironmentRevision.Missing
                && environmentDraft.Contents == "APP_NAME=Example\nAPP_ENV=local\n",
            "project .env editor starts a missing file from .env.example"
        );
        var savedEnvironmentDocument = ProjectEnvironmentFile.Save(
            editorProject,
            environmentDraft.Contents + "APP_DEBUG=true\n",
            environmentDraft.Revision
        );
        Check(
            savedEnvironmentDocument.Exists
                && !savedEnvironmentDocument.LoadedFromExample
                && savedEnvironmentDocument.Revision.Digest is not null
                && ProjectEnvironmentFile.Load(editorProject) == savedEnvironmentDocument,
            "project .env editor saves and reloads UTF-8 content"
        );
        await File.WriteAllTextAsync(
            Path.Combine(editorProject, ".env"),
            "APP_NAME=External\n"
        );
        try
        {
            ProjectEnvironmentFile.Save(
                editorProject,
                "APP_NAME=HerdMe\n",
                savedEnvironmentDocument.Revision
            );
            Check(false, "project .env editor rejects an external change");
        }
        catch (ProjectEnvironmentChangedException)
        {
            Check(
                await File.ReadAllTextAsync(Path.Combine(editorProject, ".env"))
                    == "APP_NAME=External\n",
                "project .env editor preserves an external change after a conflict"
            );
        }
        try
        {
            ProjectEnvironmentFile.Save(
                editorProject,
                new string('x', ProjectEnvironmentFile.MaximumFileBytes + 1),
                ProjectEnvironmentFile.Load(editorProject).Revision
            );
            Check(false, "project .env editor rejects oversized content");
        }
        catch (InvalidDataException)
        {
            Check(
                await File.ReadAllTextAsync(Path.Combine(editorProject, ".env"))
                    == "APP_NAME=External\n",
                "project .env editor leaves the existing file unchanged after an oversized save"
            );
        }
        var rustFsEnvironment = ServiceEnvironmentConfiguration.Variables(
            new ManagedServiceInstance
            {
                DefinitionId = "rustfs",
                Name = "RustFS",
                Port = 9_000
            },
            environmentCredentials
        ).ToDictionary(variable => variable.Key, variable => variable.Value);
        Check(
            rustFsEnvironment["AWS_ACCESS_KEY_ID"] == environmentCredentials.Username
                && rustFsEnvironment["AWS_SECRET_ACCESS_KEY"] == environmentCredentials.Secret
                && rustFsEnvironment["AWS_ENDPOINT"] == "http://127.0.0.1:9000",
            "RustFS .env variables match its managed local credentials"
        );
        var mysqlEnvironment = ServiceEnvironmentConfiguration.Variables(
            environmentInstance,
            environmentCredentials
        ).ToDictionary(variable => variable.Key, variable => variable.Value);
        Check(
            mysqlEnvironment["DB_USERNAME"] == environmentCredentials.Username
                && mysqlEnvironment["DB_PASSWORD"] == environmentCredentials.Secret,
            "database .env variables use the managed credentials"
        );
        foreach (var definition in ManagedServiceCatalog.All)
        {
            Check(
                ServiceEnvironmentConfiguration.Variables(
                    new ManagedServiceInstance
                    {
                        DefinitionId = definition.Id,
                        Name = definition.Name,
                        Port = definition.DefaultPort
                    },
                    environmentCredentials
                ).Count > 0,
                $"service catalog exposes .env variables for {definition.Id}"
            );
        }
        var unsupportedInstaller = new ServicePackageInstaller();
        foreach (var definition in new[] { valkeyDefinition, typesenseDefinition })
        {
            try
            {
                _ = unsupportedInstaller.ResolveReleaseAsync(definition.Id);
                Check(false, $"{definition.Name} package resolution rejects unavailable native Windows packages");
            }
            catch (NotSupportedException error)
            {
                Check(
                    error.Message == definition.UnavailableReason,
                    $"{definition.Name} package resolution returns its availability reason"
                );
            }
        }
        var mongoMetadata = """
            {
              "versions": [
                {
                  "version": "8.0.27",
                  "development_release": false,
                  "downloads": [{
                    "arch": "x86_64",
                    "target": "windows",
                    "edition": "base",
                    "archive": {
                      "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                      "url": "https://fastdl.mongodb.org/windows/mongodb-windows-x86_64-8.0.27.zip"
                    }
                  }]
                },
                {
                  "version": "8.0.28",
                  "development_release": false,
                  "downloads": [{
                    "arch": "x86_64",
                    "target": "windows",
                    "edition": "base",
                    "archive": {
                      "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                      "url": "https://fastdl.mongodb.org/windows/mongodb-windows-x86_64-8.0.28.zip"
                    }
                  }]
                }
              ]
            }
            """;
        var mongoRelease = ServicePackageInstaller.SelectMongoDbRelease(mongoMetadata);
        Check(mongoRelease.Version == "8.0.28", "MongoDB resolver selects the latest 8.0 LTS release");
        Check(mongoRelease.IsZipArchive, "MongoDB resolver selects the official ZIP package");
        Check(
            mongoRelease.DownloadUri.Host == "fastdl.mongodb.org",
            "MongoDB resolver accepts only the official Community download host"
        );
        var mysqlMetadata = """
            <tr><td class="sub-text">(mysql-9.7.0-winx64.zip)</td>
            <td>MD5: <code class="md5">11111111111111111111111111111111</code></td></tr>
            <tr><td class="sub-text">(mysql-9.7.1-winx64.zip)</td>
            <td>MD5: <code class="md5">8778341c62eb2ab1a95b1f22bee70f9e</code></td></tr>
            <tr><td class="sub-text">(mysql-9.7.1-winx64-debug-test.zip)</td>
            <td>MD5: <code class="md5">22222222222222222222222222222222</code></td></tr>
            """;
        var mysqlRelease = ServicePackageInstaller.SelectMySqlRelease(mysqlMetadata);
        Check(mysqlRelease.Version == "9.7.1", "MySQL resolver selects the latest Windows x64 ZIP");
        Check(
            mysqlRelease.ChecksumAlgorithm == ServicePackageChecksumAlgorithm.Md5,
            "MySQL resolver records the checksum algorithm published by the vendor"
        );
        Check(
            mysqlRelease.Checksum == "8778341c62eb2ab1a95b1f22bee70f9e",
            "MySQL resolver preserves the published vendor checksum"
        );
        Check(
            mysqlRelease.DownloadUri.AbsoluteUri
                == "https://cdn.mysql.com/Downloads/MySQL-9.7/mysql-9.7.1-winx64.zip",
            "MySQL resolver constructs only the official CDN URL"
        );
        var postgreSqlRelease = await ServicePackageInstaller.ResolvePostgreSqlReleaseAsync();
        Check(postgreSqlRelease.Version == "18.4", "PostgreSQL resolver pins the verified Windows release");
        Check(
            postgreSqlRelease.ChecksumAlgorithm == ServicePackageChecksumAlgorithm.Sha256
                && postgreSqlRelease.Checksum
                    == "02e239529ed7833d169f98d915d3feffe0813264b08b3ae353e78e8b9c97e1a6",
            "PostgreSQL resolver requires the verified full-archive SHA-256"
        );
        Check(
            postgreSqlRelease.DownloadUri.Host == "get.enterprisedb.com",
            "PostgreSQL resolver uses the documented EDB binary host"
        );
        var redisMetadata = """
            {
              "draft": false,
              "tag_name": "8.8.1",
              "assets": [{
                "name": "Redis-8.8.1-Windows-x64-msys2.zip",
                "digest": "sha256:1a0741a8f997a50ad7a32370e9ddf719ed3d5d87701324c57b7b34518b980460",
                "browser_download_url": "https://github.com/redis-windows/redis-windows/releases/download/8.8.1/Redis-8.8.1-Windows-x64-msys2.zip"
              }]
            }
            """;
        var redisRelease = ServicePackageInstaller.SelectRedisRelease(redisMetadata);
        Check(redisRelease.Version == "8.8.1", "Redis resolver preserves the stable release version");
        Check(redisRelease.IsZipArchive, "Redis resolver selects the portable Windows ZIP");
        Check(
            redisRelease.ChecksumAlgorithm == ServicePackageChecksumAlgorithm.Sha256
                && redisRelease.Checksum == "1a0741a8f997a50ad7a32370e9ddf719ed3d5d87701324c57b7b34518b980460",
            "Redis resolver requires the GitHub SHA-256 digest"
        );
        Check(
            redisRelease.DownloadUri.Host == "github.com",
            "Redis resolver accepts only the expected GitHub release host"
        );
        var rustFsMetadata = """
            [{
              "draft": false,
              "tag_name": "1.0.0-beta.11-preview.1",
              "assets": [{
                "name": "rustfs-windows-x86_64-v1.0.0-beta.11-preview.1.zip",
                "digest": "sha256:9e60cd6a71e8c18e80a94890576fd62cd55ade67c1476381f931ae7fc294f83a",
                "browser_download_url": "https://github.com/rustfs/rustfs/releases/download/1.0.0-beta.11-preview.1/rustfs-windows-x86_64-v1.0.0-beta.11-preview.1.zip"
              }]
            }]
            """;
        var rustFsRelease = ServicePackageInstaller.SelectRustFsRelease(rustFsMetadata);
        Check(rustFsRelease.Version == "1.0.0-beta.11-preview.1", "RustFS resolver preserves its release version");
        Check(rustFsRelease.IsZipArchive, "RustFS resolver selects the official Windows ZIP");
        Check(
            rustFsRelease.ChecksumAlgorithm == ServicePackageChecksumAlgorithm.Sha256
                && rustFsRelease.Checksum == "9e60cd6a71e8c18e80a94890576fd62cd55ade67c1476381f931ae7fc294f83a",
            "RustFS resolver requires the official GitHub SHA-256 digest"
        );
        var rootArchive = Path.Combine(supportRoot, "root-archive");
        Directory.CreateDirectory(rootArchive);
        await File.WriteAllBytesAsync(Path.Combine(rootArchive, "rustfs.exe"), []);
        var normalizedRustFs = ServicePackageInstaller.NormalizeExtractedRuntime(rootArchive, "rustfs");
        Check(
            File.Exists(Path.Combine(normalizedRustFs, "rustfs.exe")),
            "service ZIP normalization accepts an executable at the archive root"
        );
        var postgreSqlArchive = Path.Combine(supportRoot, "postgresql-archive");
        var postgreSqlBin = Path.Combine(postgreSqlArchive, "pgsql", "bin");
        Directory.CreateDirectory(postgreSqlBin);
        await File.WriteAllBytesAsync(Path.Combine(postgreSqlBin, "postgres.exe"), []);
        var normalizedPostgreSql = ServicePackageInstaller.NormalizeExtractedRuntime(
            postgreSqlArchive,
            "postgresql"
        );
        Check(
            File.Exists(Path.Combine(normalizedPostgreSql, "bin", "postgres.exe")),
            "service ZIP normalization accepts PostgreSQL's pgsql archive root"
        );
        var mongoInstance = new ManagedServiceInstance
        {
            DefinitionId = "mongodb",
            Name = "MongoDB",
            Port = 27_018
        };
        var mongoExecutable = Path.Combine("C:\\HerdMe", "mongodb", "bin", "mongod.exe");
        var mongoData = Path.Combine("C:\\HerdMe", "Services", "mongo", "data");
        var mongoLaunch = WindowsServiceManager.BuildLaunchSpec(mongoInstance, mongoExecutable, mongoData);
        Check(mongoLaunch.Arguments.SequenceEqual([
            "--dbpath",
            mongoData,
            "--bind_ip",
            "127.0.0.1",
            "--port",
            "27018"
        ]), "MongoDB launch is loopback-only and uses the configured data and port");
        var mysqlInstance = new ManagedServiceInstance
        {
            DefinitionId = "mysql",
            Name = "MySQL",
            Port = 3_307
        };
        var mysqlExecutable = Path.Combine("C:\\HerdMe", "mysql", "bin", "mysqld.exe");
        var mysqlData = Path.Combine("C:\\HerdMe", "Services", "mysql", "data");
        var mysqlRuntime = Directory.GetParent(Path.GetDirectoryName(mysqlExecutable)!)!.FullName;
        var mysqlLaunch = WindowsServiceManager.BuildLaunchSpec(mysqlInstance, mysqlExecutable, mysqlData);
        Check(mysqlLaunch.Arguments.SequenceEqual([
            "--no-defaults",
            "--console",
            "--basedir=" + mysqlRuntime,
            "--datadir=" + mysqlData,
            "--port=3307",
            "--bind-address=127.0.0.1",
            "--mysqlx=0",
            "--pid-file=" + Path.Combine(mysqlData, "mysql.pid")
        ]), "MySQL launch disables its extra listener and binds its SQL port to loopback");
        var mariaDbInstance = new ManagedServiceInstance
        {
            DefinitionId = "mariadb",
            Name = "MariaDB",
            Port = 3_306
        };
        var mariaDbExecutable = Path.Combine("C:\\HerdMe", "mariadb", "bin", "mariadbd.exe");
        var mariaDbData = Path.Combine("C:\\HerdMe", "Services", "mariadb", "data");
        var mariaDbRuntime = Directory.GetParent(Path.GetDirectoryName(mariaDbExecutable)!)!.FullName;
        var mariaDbLaunch = WindowsServiceManager.BuildLaunchSpec(
            mariaDbInstance,
            mariaDbExecutable,
            mariaDbData
        );
        Check(mariaDbLaunch.Arguments.SequenceEqual([
            "--no-defaults",
            "--console",
            "--basedir=" + mariaDbRuntime,
            "--datadir=" + mariaDbData,
            "--port=3306",
            "--bind-address=127.0.0.1",
            "--pid-file=" + Path.Combine(mariaDbData, "mariadb.pid")
        ]), "MariaDB launch binds its SQL port to loopback without disabling local account matching");
        var mariaDbInitialization = WindowsServiceManager.BuildMariaDbInitializationArguments(
            mariaDbData,
            mariaDbInstance.Port,
            environmentCredentials
        );
        Check(mariaDbInitialization.SequenceEqual([
            "--datadir=" + mariaDbData,
            "--password=" + environmentCredentials.Secret,
            "--port=3306"
        ]), "MariaDB initialization uses only options supported by its Windows initializer");
        var postgreSqlInstance = new ManagedServiceInstance
        {
            DefinitionId = "postgresql",
            Name = "PostgreSQL",
            Port = 5_433
        };
        var postgreSqlExecutable = Path.Combine(
            "C:\\HerdMe", "postgresql", "bin", "postgres.exe"
        );
        var postgreSqlData = Path.Combine("C:\\HerdMe", "Services", "postgresql", "data");
        var postgreSqlLaunch = WindowsServiceManager.BuildLaunchSpec(
            postgreSqlInstance,
            postgreSqlExecutable,
            postgreSqlData
        );
        Check(postgreSqlLaunch.Arguments.SequenceEqual([
            "-D",
            postgreSqlData,
            "-h",
            "127.0.0.1",
            "-p",
            "5433"
        ]), "PostgreSQL launch binds only to loopback and uses its isolated data directory");
        var redisInstance = new ManagedServiceInstance
        {
            DefinitionId = "redis",
            Name = "Redis",
            Port = 6_380
        };
        var redisData = Path.Combine("C:\\HerdMe", "Services", "redis", "data");
        var redisLaunch = WindowsServiceManager.BuildLaunchSpec(
            redisInstance,
            Path.Combine("C:\\HerdMe", "redis", "redis-server.exe"),
            redisData
        );
        Check(
            redisLaunch.Arguments.SequenceEqual([
                "--bind",
                "127.0.0.1",
                "--port",
                "6380",
                "--dir",
                redisData.Replace('\\', '/'),
                "--protected-mode",
                "yes",
                "--appendonly",
                "yes",
                "--daemonize",
                "no"
            ]),
            "Redis launch is persistent, protected, and loopback-only"
        );
        var rustFsInstance = new ManagedServiceInstance
        {
            DefinitionId = "rustfs",
            Name = "RustFS",
            Port = 9_001
        };
        var rustFsData = Path.Combine("C:\\HerdMe", "Services", "rustfs", "data");
        var rustFsCredentials = ServiceCredentialGenerator.Create(rustFsInstance.Id);
        var otherCredentials = ServiceCredentialGenerator.Create(Guid.NewGuid());
        Check(
            rustFsCredentials.IsValid
                && otherCredentials.IsValid
                && rustFsCredentials.Secret != otherCredentials.Secret
                && rustFsCredentials.Username != otherCredentials.Username
                && !rustFsCredentials.Secret.Contains("herdme-local-service", StringComparison.Ordinal),
            "managed service credentials are random, valid, and unique per instance"
        );
        var rustFsLaunch = WindowsServiceManager.BuildLaunchSpec(
            rustFsInstance,
            Path.Combine("C:\\HerdMe", "rustfs", "rustfs.exe"),
            rustFsData,
            9_002,
            rustFsCredentials
        );
        Check(
            rustFsLaunch.Arguments.SequenceEqual([
                "server",
                rustFsData,
                "--address",
                "127.0.0.1:9001",
                "--console-address",
                "127.0.0.1:9002"
            ]),
            "RustFS launch binds its API and console to loopback"
        );
        Check(
            rustFsLaunch.Environment["RUSTFS_ACCESS_KEY"] == rustFsCredentials.Username
                && rustFsLaunch.Environment["RUSTFS_SECRET_KEY"] == rustFsCredentials.Secret,
            "RustFS launch credentials match generated .env values"
        );
        var rustFsLaunchEnvironment = ServiceEnvironmentConfiguration.Variables(
            rustFsInstance,
            rustFsCredentials
        ).ToDictionary(variable => variable.Key, variable => variable.Value);
        Check(
            rustFsLaunch.Environment["RUSTFS_ACCESS_KEY"]
                    == rustFsLaunchEnvironment["AWS_ACCESS_KEY_ID"]
                && rustFsLaunch.Environment["RUSTFS_SECRET_KEY"]
                    == rustFsLaunchEnvironment["AWS_SECRET_ACCESS_KEY"],
            "RustFS launch and .env export use exactly the same managed credentials"
        );
        var tablePlusMySql = TablePlusConnection.UriFor(
            new ManagedServiceInstance
            {
                DefinitionId = "mysql",
                Name = "MySQL",
                Port = 3_307
            },
            environmentCredentials
        );
        var tablePlusPostgreSql = TablePlusConnection.UriFor(
            new ManagedServiceInstance
            {
                DefinitionId = "postgresql",
                Name = "PostgreSQL",
                Port = 5_433
            },
            environmentCredentials
        );
        var tablePlusRedis = TablePlusConnection.UriFor(new ManagedServiceInstance
        {
            DefinitionId = "valkey",
            Name = "Valkey",
            Port = 6_380
        });
        Check(
            tablePlusMySql is not null
                && tablePlusMySql.Scheme == "mysql"
                && tablePlusMySql.UserInfo.Contains(environmentCredentials.Username, StringComparison.Ordinal)
                && tablePlusMySql.UserInfo.Contains(environmentCredentials.Secret, StringComparison.Ordinal)
                && tablePlusMySql.Host == "127.0.0.1"
                && tablePlusMySql.Port == 3_307
                && tablePlusMySql.AbsolutePath == "/mysql",
            "Windows TablePlus MySQL links use the managed loopback connection"
        );
        Check(
            TablePlusConnection.DisplayAddress(new ManagedServiceInstance
            {
                DefinitionId = "mysql",
                Name = "MySQL",
                Port = 3_307
            }) == "mysql://127.0.0.1:3307/mysql"
                && !TablePlusConnection.DisplayAddress(new ManagedServiceInstance
                {
                    DefinitionId = "mysql",
                    Name = "MySQL",
                    Port = 3_307
                })!.Contains(environmentCredentials.Secret, StringComparison.Ordinal),
            "Windows service rows reveal a password-free database connection address"
        );
        Check(
            tablePlusPostgreSql is not null
                && tablePlusPostgreSql.Scheme == "postgresql"
                && tablePlusPostgreSql.UserInfo.Contains(environmentCredentials.Username, StringComparison.Ordinal)
                && tablePlusPostgreSql.UserInfo.Contains(environmentCredentials.Secret, StringComparison.Ordinal)
                && tablePlusPostgreSql.Port == 5_433
                && tablePlusPostgreSql.AbsolutePath == "/postgres",
            "Windows TablePlus PostgreSQL links preserve the managed username"
        );
        Check(
            tablePlusRedis is not null
                && tablePlusRedis.Scheme == "redis"
                && tablePlusRedis.Port == 6_380
                && tablePlusRedis.AbsolutePath == "/0"
                && TablePlusConnection.UriFor(new ManagedServiceInstance
                {
                    DefinitionId = "minio",
                    Name = "MinIO",
                    Port = 9_000
                }) is null,
            "Windows TablePlus links cover cache services and reject unsupported services"
        );
        Check(
            TablePlusConnection.DisplayAddress(new ManagedServiceInstance
            {
                DefinitionId = "minio",
                Name = "MinIO",
                Port = 9_000
            }) is null,
            "Windows service rows hide connection addresses for unsupported services"
        );
        Check(
            TablePlusConnection.UriFor(new ManagedServiceInstance
            {
                DefinitionId = "mysql",
                Name = "MySQL",
                Port = 3_306
            }) is null,
            "database TablePlus links require managed credentials"
        );
        var mysqlProvisioningSql = DatabaseServiceAuthenticator.MySqlProvisioningSql(
            environmentCredentials
        );
        Check(
            mysqlProvisioningSql.Contains(
                $"'{environmentCredentials.Username}'@'127.0.0.1'",
                StringComparison.Ordinal
            )
                && mysqlProvisioningSql.Contains(
                    $"IDENTIFIED BY '{environmentCredentials.Secret}'",
                    StringComparison.Ordinal
                )
                && mysqlProvisioningSql.Contains("ALTER USER 'root'@'localhost'", StringComparison.Ordinal)
                && mysqlProvisioningSql.Contains("DELETE FROM mysql.user WHERE User = ''", StringComparison.Ordinal),
            "MySQL provisioning creates managed credentials and removes passwordless accounts"
        );
        var securedHba = DatabaseServiceAuthenticator.SecurePostgreSqlHba(
            "# keep\r\nlocal all all trust\r\nhost all all 127.0.0.1/32 trust # loopback\r\nhostssl all all ::1/128 scram-sha-256\r\n"
        );
        Check(
            securedHba.Changed
                && !securedHba.Contents.Contains(" all trust", StringComparison.Ordinal)
                && securedHba.Contents.Contains("scram-sha-256 # loopback", StringComparison.Ordinal)
                && securedHba.Contents.Contains("# keep\r\n", StringComparison.Ordinal),
            "PostgreSQL migration replaces trust rules while preserving comments and CRLF"
        );
        var currentServiceRow = new ManagedServiceRow
        {
            Id = Guid.NewGuid(),
            DefinitionId = "mongodb",
            Name = "MongoDB",
            Port = 27_017,
            Version = "8.0.28",
            State = ManagedServiceState.Stopped,
            Status = "Stopped",
            InstallLabel = "Update",
            ToggleLabel = "Start",
            StartAutomatically = false,
            IsUpdateAvailable = false
        };
        Check(!currentServiceRow.CanInstallOrUpdate, "current service releases hide the update button");
        Check(!currentServiceRow.CanOpenInTablePlus, "stopped databases hide the TablePlus action");
        var outdatedServiceRow = new ManagedServiceRow
        {
            Id = Guid.NewGuid(),
            DefinitionId = "mongodb",
            Name = "MongoDB",
            Port = 27_017,
            Version = "8.0.27",
            State = ManagedServiceState.Stopped,
            Status = "Stopped",
            InstallLabel = "Update",
            ToggleLabel = "Start",
            StartAutomatically = false,
            IsUpdateAvailable = true
        };
        Check(outdatedServiceRow.CanInstallOrUpdate, "outdated service releases expose the update button");
        var runningStorageRow = new ManagedServiceRow
        {
            Id = Guid.NewGuid(),
            DefinitionId = "rustfs",
            Name = "RustFS",
            Port = 9_001,
            Version = "1.0.0-beta.11-preview.1",
            State = ManagedServiceState.Running,
            Status = "Running",
            InstallLabel = "Update",
            ToggleLabel = "Stop",
            StartAutomatically = false,
            IsUpdateAvailable = false,
            ConsolePort = 9_002
        };
        Check(runningStorageRow.CanOpenConsole, "running storage services expose their console action");
        Check(!currentServiceRow.CanOpenConsole, "services without a console hide the console action");
        var runningDatabaseRow = new ManagedServiceRow
        {
            Id = Guid.NewGuid(),
            DefinitionId = "mongodb",
            Name = "MongoDB",
            Port = 27_017,
            Version = "8.0.28",
            State = ManagedServiceState.Running,
            Status = "Running",
            InstallLabel = "Update",
            ToggleLabel = "Stop",
            StartAutomatically = false,
            IsUpdateAvailable = false
        };
        Check(runningDatabaseRow.CanOpenInTablePlus, "running databases expose the TablePlus action");

        var originalHosts = "127.0.0.1 localhost\n"
            + "# User entry\n"
            + "# BEGIN HerdMe local sites\n127.0.0.1 old.test\n# END HerdMe local sites\n"
            + "10.0.0.5 intranet.test\n";
        var hosts = WindowsHostsManager.Render(
            originalHosts,
            [" Store.Local-Test. ", "api.local-test", "store.local-test", "not a domain"]
        );
        Check(hosts.Contains("# User entry\r\n"), "hosts rendering preserves user entries");
        Check(!hosts.Contains("old.test"), "hosts rendering replaces the previous managed block");
        Check(hosts.Contains("127.0.0.1\tapi.local-test\r\n"), "hosts rendering normalizes valid domains");
        Check(hosts.Split("store.local-test").Length == 2, "hosts rendering removes duplicate domains");
        Check(!hosts.Contains("not a domain"), "hosts rendering rejects invalid domains");
        var hostsWithoutSites = WindowsHostsManager.Render(hosts, []);
        Check(!hostsWithoutSites.Contains("BEGIN HerdMe"), "hosts rendering removes an unused managed block");
        Check(hostsWithoutSites.Contains("10.0.0.5 intranet.test"), "hosts cleanup preserves unrelated mappings");
        Check(WindowsHostsManager.ContainsManagedBlock(hosts), "managed hosts blocks are detected");
        Check(!WindowsHostsManager.ContainsManagedBlock(hostsWithoutSites), "removed hosts blocks are not reported");
        Check(
            WindowsHostsManager.IsAllowedHostsUpdate(originalHosts, hosts),
            "the elevated hosts helper accepts a render that changes only HerdMe's block"
        );
        Check(
            WindowsHostsManager.IsAllowedHostsUpdate(hosts, hostsWithoutSites),
            "the elevated hosts helper accepts removal of HerdMe's block"
        );
        Check(
            !WindowsHostsManager.IsAllowedHostsUpdate(
                originalHosts,
                hosts.Replace("# User entry", "203.0.113.7 poisoned.example")
            ),
            "the elevated hosts helper rejects changes outside HerdMe's block"
        );
        Check(
            !WindowsHostsManager.IsAllowedHostsUpdate(
                originalHosts,
                hosts.Replace("127.0.0.1\tapi.local-test", "192.0.2.1\tapi.local-test")
            ),
            "the elevated hosts helper rejects non-loopback managed mappings"
        );
        Throws<InvalidDataException>(
            () => WindowsHostsManager.Render(
                "127.0.0.1 localhost\n# BEGIN HerdMe local sites\n127.0.0.1 broken.test\n",
                ["api.local-test"]
            ),
            "hosts rendering refuses an unterminated managed block instead of deleting trailing entries"
        );
        var helperSupport = Path.Combine(supportRoot, "helper");
        var helperWindows = Path.Combine(supportRoot, "windows");
        var allowedSource = Path.Combine(helperSupport, "Cache", "hosts", "hosts-1234");
        var allowedDestination = Path.Combine(helperWindows, "System32", "drivers", "etc", "hosts");
        Check(
            WindowsHostsManager.IsAllowedHelperRequest(
                allowedSource,
                allowedDestination,
                helperSupport,
                helperWindows
            ),
            "the elevated hosts helper accepts only HerdMe's staged hosts file"
        );
        Check(
            !WindowsHostsManager.IsAllowedHelperRequest(
                Path.Combine(supportRoot, "outside-hosts"),
                allowedDestination,
                helperSupport,
                helperWindows
            ),
            "the elevated hosts helper rejects sources outside HerdMe's cache"
        );
        Check(
            !WindowsHostsManager.IsAllowedHelperRequest(
                allowedSource,
                Path.Combine(supportRoot, "unrelated-file"),
                helperSupport,
                helperWindows
            ),
            "the elevated hosts helper rejects destinations other than Windows hosts"
        );
        Check(
            !WindowsHostsManager.IsAllowedHelperRequest(
                "\0invalid",
                allowedDestination,
                helperSupport,
                helperWindows
            ),
            "the elevated hosts helper rejects malformed source paths without crashing"
        );
        var stagedCandidate = Path.Combine(supportRoot, "staged-hosts-candidate");
        await File.WriteAllTextAsync(stagedCandidate, hosts, new UTF8Encoding(false));
        Check(
            WindowsHostsManager.ReadStagedCandidate(stagedCandidate) == hosts,
            "the elevated hosts helper reads a bounded regular UTF-8 candidate"
        );
        var oversizedCandidate = Path.Combine(supportRoot, "oversized-hosts-candidate");
        await using (var oversized = new FileStream(
            oversizedCandidate,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None
        ))
        {
            oversized.SetLength(WindowsHostsManager.MaximumStagedHostsBytes + 1);
        }
        Throws<InvalidDataException>(
            () => WindowsHostsManager.ReadStagedCandidate(oversizedCandidate),
            "the elevated hosts helper rejects oversized staged files"
        );
        var invalidUtf8Candidate = Path.Combine(supportRoot, "invalid-utf8-hosts-candidate");
        await File.WriteAllBytesAsync(invalidUtf8Candidate, [0xC3, 0x28]);
        Throws<InvalidDataException>(
            () => WindowsHostsManager.ReadStagedCandidate(invalidUtf8Candidate),
            "the elevated hosts helper rejects invalid UTF-8 without crashing"
        );
        var stagedDirectory = Path.Combine(supportRoot, "staged-hosts-directory");
        Directory.CreateDirectory(stagedDirectory);
        Throws<InvalidDataException>(
            () => WindowsHostsManager.ReadStagedCandidate(stagedDirectory),
            "the elevated hosts helper rejects staged directories and reparse-like entries"
        );

        var runtimeSite = Path.Combine(supportRoot, "RuntimeSite");
        Directory.CreateDirectory(runtimeSite);
        var runtimeStore = new SiteRuntimeStore();
        runtimeStore.SetPhp(runtimeSite, " 8.4 ");
        runtimeStore.SetNode(runtimeSite, "v24.1.0");
        Check(File.ReadAllText(Path.Combine(runtimeSite, ".herdme-php")).Trim() == "8.4", "PHP site runtime is persisted");
        Check(File.ReadAllText(Path.Combine(runtimeSite, ".herdme-node")).Trim() == "v24.1.0", "Node site runtime is persisted");
        Throws<ArgumentException>(() => runtimeStore.SetPhp(runtimeSite, "8.x"), "invalid PHP cycles are rejected");
        Throws<ArgumentException>(() => runtimeStore.SetNode(runtimeSite, "current"), "invalid Node versions are rejected");
        runtimeStore.SetPhp(runtimeSite, null);
        runtimeStore.SetNode(runtimeSite, " ");
        Check(!File.Exists(Path.Combine(runtimeSite, ".herdme-php")), "PHP site runtime can return to its default");
        Check(!File.Exists(Path.Combine(runtimeSite, ".herdme-node")), "Node site runtime can return to its default");

        var message = CapturedMail.Parse(
            "sender@example.test",
            ["recipient@example.test"],
            "From: sender@example.test\r\n"
            + "Subject: =?UTF-8?B?2YXYsdit2KjYpw==?=\r\n"
            + "Content-Type: multipart/alternative; boundary=herdme\r\n\r\n"
            + "--herdme\r\nContent-Type: text/plain; charset=utf-8\r\n"
            + "Content-Transfer-Encoding: quoted-printable\r\n\r\nHello=20mail\r\n"
            + "--herdme\r\nContent-Type: text/html; charset=utf-8\r\n"
            + "Content-Transfer-Encoding: base64\r\n\r\nPGI+SGVsbG8gbWFpbDwvYj4=\r\n"
            + "--herdme--\r\n"
        );
        Check(message.Subject == "مرحبا", "mail headers decode RFC 2047 text");
        Check(message.Body == "Hello mail", "mail quoted-printable text decodes");
        Check(message.MatchesSearch(""), "empty mail searches keep every message");
        Check(message.MatchesSearch("SENDER@EXAMPLE.TEST"), "mail searches match senders without case sensitivity");
        Check(message.MatchesSearch("مرحبا"), "mail searches match decoded subjects");
        Check(message.MatchesSearch("recipient@example.test"), "mail searches match recipients");
        Check(!message.MatchesSearch("Hello mail"), "mail searches do not inspect private message bodies");
        Check(!message.MatchesSearch("does-not-exist"), "mail searches reject unrelated metadata");
        var htmlBody = message.HtmlBody ?? throw new InvalidOperationException("Failed contract: mail HTML is present");
        Check(htmlBody == "<b>Hello mail</b>", "mail Base64 HTML decodes");
        var safeMailPreview = MailMimeParser.SafeHtmlDocument(htmlBody);
        Check(safeMailPreview.Contains("default-src 'none'"), "mail HTML preview blocks external content");
        Check(safeMailPreview.Contains("form-action 'none'"), "mail HTML preview blocks forms");
        Check(safeMailPreview.Contains("frame-src 'none'"), "mail HTML preview blocks frames");
        Check(
            safeMailPreview.Contains("style-src 'sha256-48hOXKVM1rwpXip/9XRIr0XijcrNP/RHiD+a7aSGrzg='")
                && !safeMailPreview.Contains("unsafe-inline"),
            "mail HTML preview allows only HerdMe's hashed stylesheet"
        );
        Check(
            MailMimeParser.IsPreviewNavigationAllowed("about:blank")
                && MailMimeParser.IsPreviewNavigationAllowed("about:blank#message")
                && !MailMimeParser.IsPreviewNavigationAllowed("https://example.test")
                && !MailMimeParser.IsPreviewNavigationAllowed("data:text/html,unsafe"),
            "mail HTML preview navigation remains inside its generated document"
        );

        await TestMailCaptureAsync(supportRoot);
        await TestDumpCaptureAsync(supportRoot);
        await TestFastCgiClientAsync();
        await TestLocalHttpSiteServerAsync(supportRoot);
        await TestCancelledCommandKillsTreeAsync(supportRoot);
    }
}
