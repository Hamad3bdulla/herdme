using HerdMe.Windows.Services;

internal static partial class ContractChecks
{
    internal static void VerifyProjectManifestContracts(string supportRoot)
    {
        VerifyManifestParsing();
        VerifyManifestRejections();
        VerifyManifestRoundTrip(Path.Combine(supportRoot, "manifest-project"));
        VerifyManifestSuggestion();
    }

    private static void VerifyManifestParsing()
    {
        var manifest = ProjectManifestFile.Parse(
            "\uFEFF# team settings\r\n"
                + "php: \"8.3\"   # pinned\r\n"
                + "node: v22.11.0\r\n"
                + "services:\r\n"
                + "  - MariaDB\r\n"
                + "  - redis # cache\r\n"
                + "\r\n"
                + "database: 'my_app'\r\n"
        );
        Check(
            manifest.Php == "8.3"
                && manifest.Node == "22.11.0"
                && manifest.Services.SequenceEqual(["mariadb", "redis"])
                && manifest.Database == "my_app"
                && manifest.DatabaseService == "mariadb",
            "herdme.yml parses runtimes, a block service list, comments, quotes, and CRLF"
        );
        var inline = ProjectManifestFile.Parse("services: [postgresql, valkey]\ndatabase: shop\nnode: 22\n");
        Check(
            inline.Services.SequenceEqual(["postgresql", "valkey"])
                && inline.DatabaseService == "postgresql"
                && inline.Node == "22"
                && inline.Php is null,
            "herdme.yml accepts inline service lists and a Node.js major version"
        );
        Check(
            ProjectManifestFile.Parse("# nothing yet\n").IsEmpty
                && ProjectManifestFile.Parse("services: []\n").IsEmpty,
            "herdme.yml without settings is empty"
        );
    }

    private static void VerifyManifestRejections()
    {
        string[] invalid =
        [
            "php: 8.3\nphp: 8.4\n",
            "phpversion: 8.3\n",
            "php: 8\n",
            "php: \"8.3; rm -rf\"\n",
            "node: latest\n",
            "services: [mariadb, mariadb]\n",
            "services: [nginx]\n",
            "services: mariadb\n",
            "services:\n\t- redis\n",
            "  php: 8.3\n",
            "- redis\n",
            "php: \"8.3\n",
            "database: my-app\nservices: [mariadb]\n",
            "database: app\nservices: [redis]\n",
            "database: app\n",
            "php:\n",
            "php 8.3\n",
            "php: !!python/object 8.3\n",
            "services:\n  - redis\n  nested: true\n",
            new string('#', ProjectManifestFile.MaximumFileBytes + 1)
        ];
        foreach (var text in invalid)
        {
            Throws<FormatException>(
                () => ProjectManifestFile.Parse(text),
                "herdme.yml rejects invalid input: " + text.Replace("\n", "\\n")[..Math.Min(40, text.Length)]
            );
        }
        try
        {
            ProjectManifestFile.Parse("php: \"8.3\"\nweird: 1\n");
            Check(false, "herdme.yml reports the line of an unknown setting");
        }
        catch (ProjectManifestException error)
        {
            Check(
                error.Line == 2 && error.Message.Contains("weird", StringComparison.Ordinal),
                "herdme.yml reports the line of an unknown setting"
            );
        }
    }

    private static void VerifyManifestRoundTrip(string root)
    {
        Directory.CreateDirectory(root);
        Check(ProjectManifestFile.Load(root) is null, "herdme.yml is optional");
        var manifest = new ProjectManifest("8.4", "22.11.0", ["mysql", "redis"], "blog");
        ProjectManifestFile.Save(root, manifest);
        var loaded = ProjectManifestFile.Load(root);
        Check(
            loaded is not null
                && loaded.Php == manifest.Php
                && loaded.Node == manifest.Node
                && loaded.Services.SequenceEqual(manifest.Services)
                && loaded.Database == manifest.Database,
            "herdme.yml export can be applied again without changes"
        );
        Check(
            !File.Exists(ProjectManifestFile.PathFor(root) + ".tmp")
                && !File.ReadAllText(ProjectManifestFile.PathFor(root)).Contains('\r'),
            "herdme.yml is written atomically with LF line endings"
        );
        Throws<FormatException>(
            () => ProjectManifestFile.Save(root, new ProjectManifest("8.4", null, ["unknown"], null)),
            "herdme.yml export never writes a file HerdMe would reject"
        );
        Check(
            ProjectManifestFile.Load(root)?.Database == "blog",
            "a rejected herdme.yml export leaves the existing file untouched"
        );
        File.WriteAllText(ProjectManifestFile.PathFor(root), new string('#', ProjectManifestFile.MaximumFileBytes + 10));
        Throws<ProjectManifestException>(
            () => ProjectManifestFile.Load(root),
            "herdme.yml refuses oversized files before reading them"
        );

        var runtimes = new SiteRuntimeStore();
        runtimes.SetPhp(root, "8.3");
        runtimes.SetNode(root, "22.11.0");
        Check(
            runtimes.GetPhp(root) == "8.3" && runtimes.GetNode(root) == "22.11.0",
            "site runtime pins can be read back for herdme.yml export"
        );
        File.WriteAllText(Path.Combine(root, ".herdme-php"), "not-a-version");
        runtimes.SetNode(root, null);
        Check(
            runtimes.GetPhp(root) is null && runtimes.GetNode(root) is null,
            "invalid or missing runtime pins read as unset"
        );
    }

    private static void VerifyManifestSuggestion()
    {
        const string laravel =
            "APP_NAME=Shop\nDB_CONNECTION=mysql\nDB_DATABASE=\"shop_db\"\n"
                + "CACHE_STORE=redis\nREDIS_HOST=127.0.0.1\nSCOUT_DRIVER=meilisearch\n";
        var suggested = ProjectManifestFile.Suggest(
            "8.3", "v22.11.0", laravel, ["mariadb", "redis", "meilisearch", "postgresql"]
        );
        Check(
            suggested.Php == "8.3"
                && suggested.Node == "22.11.0"
                && suggested.Services.SequenceEqual(["mariadb", "redis", "meilisearch"])
                && suggested.Database == "shop_db",
            "herdme.yml export suggests the services and database the project's .env uses"
        );
        var none = ProjectManifestFile.Suggest(null, null, laravel, []);
        Check(
            none.Services.Count == 0 && none.Database is null,
            "herdme.yml export only suggests services HerdMe has configured"
        );
        Check(
            ProjectManifestFile.Parse(ProjectManifestFile.Serialize(suggested)).Database == "shop_db",
            "suggested herdme.yml files are valid"
        );
    }
}
