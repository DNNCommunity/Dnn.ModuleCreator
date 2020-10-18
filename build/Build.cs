using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Compression;
using System.Linq;
using System.Xml;
using BuildHelpers;
using Nuke.Common;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.Git;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.CompressionTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.Git.GitTasks;
using static Nuke.Common.Tools.MSBuild.MSBuildTasks;
using Nuke.Common.CI;
using System.ComponentModel.Design.Serialization;
using System.Text;
using Octokit;
using System.IO;

[CheckBuildProjectConfigurations]
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main() => Execute<Build>(x => x.Package);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Parameter("Github Token")]
    readonly string GithubToken = Environment.GetEnvironmentVariable("github-token");

    [Solution] readonly Solution Solution;
    [GitRepository] readonly GitRepository GitRepository;
    [GitVersion(Framework = "netcoreapp3.1", UpdateAssemblyInfo = false, NoFetch = true)] readonly GitVersion GitVersion;


    readonly string ModuleName = "Dnn.Modules.ModuleCreator";
    IReadOnlyCollection<string> InstallFiles;
    bool IsInDesktopModules;
    readonly AbsolutePath ArtifactsDirectory = RootDirectory / "Artifacts";
    readonly AbsolutePath StagingDirectory = RootDirectory / "Artifacts" / "Staging";
    readonly AbsolutePath DeployDirectory = RootDirectory.Parent / "Admin" / "ModuleCreator";
    readonly AbsolutePath DnnModuleInstallDirectory = RootDirectory.Parent.Parent / "Install" / "Module";
    string ModuleBranch;
    GitHubClient gitHubClient;
    string releaseNotes = "";
    string owner = "";
    string name = "";
    Release release;

    Target SetupVariables => _ => _
        .Before(Package)
        .Executes(() =>
        {
            InstallFiles = GlobFiles(RootDirectory, "*.txt", "*.dnn");
            Logger.Normal($"Found install files: {Helpers.Dump(InstallFiles)}");
            IsInDesktopModules = RootDirectory.Parent.ToString().EndsWith("DesktopModules");
            Logger.Normal(IsInDesktopModules ? "We are" : "We are not" + " in a DesktopModules folder.");
        });

    Target Clean => _ => _
        .DependsOn(SetupVariables)
        .Before(Restore)
        .Before(Package)
        .Executes(() =>
        {
            EnsureCleanDirectory(ArtifactsDirectory);
            Logger.Normal($"Cleaned {ArtifactsDirectory}");
        });

    Target Restore => _ => _
        .DependsOn(SetupVariables)
        .Executes(() =>
        {
            var project = Solution.GetProject(ModuleName);
            DotNetRestore(s => s
                .SetProjectFile(project));
        });

    Target SetManifestVersions => _ => _
        .DependsOn(SetupVariables)
        .Executes(() =>
        {
            var manifests = GlobFiles(RootDirectory, "**/*.dnn");
            manifests.ForEach(manifest =>
            {
                var doc = new XmlDocument();
                doc.Load(manifest);
                var packages = doc.SelectNodes("dotnetnuke/packages/package");
                foreach (XmlNode package in packages)
                {
                    var version = package.Attributes["version"];
                    if (version != null)
                    {
                        Logger.Normal($"Found package {package.Attributes["name"].Value} with version {version.Value}");
                        version.Value = $"{GitVersion.Major.ToString("00", CultureInfo.InvariantCulture)}.{GitVersion.Minor.ToString("00", CultureInfo.InvariantCulture)}.{GitVersion.Patch.ToString("00", CultureInfo.InvariantCulture)}";
                        Logger.Normal($"Updated packages {package.Attributes["name"].Value} to version {version.Value}");
                    }
                }

                doc.Save(manifest);
                Logger.Normal($"Saved {manifest}");
            });
        });

    Target TagRelease => _ => _
        .OnlyWhenDynamic(() => ModuleBranch == "main" || ModuleBranch.StartsWith("release"))
        .OnlyWhenDynamic(() => !string.IsNullOrEmpty(GithubToken))
        .DependsOn(SetupVariables)
        .DependsOn(SetupGitHubClient)
        .DependsOn(SetBranch)
        .Executes(() =>
        {
            var version = ModuleBranch == "main" ? GitVersion.MajorMinorPatch : GitVersion.SemVer;
            GitLogger = (type, output) => Logger.Info(output);
            Git($"tag v{version}");
            Git($"push --tags");
        });

    Target Compile => _ => _
        .DependsOn(Clean)
        .DependsOn(Restore)
        .DependsOn(SetManifestVersions)
        .DependsOn(TagRelease)
        .DependsOn(SetBranch)
        .Executes(() =>
        {
            MSBuild(s => s
                .SetProjectFile(Solution.GetProject(ModuleName))
                .SetConfiguration(Configuration)
                .SetAssemblyVersion(ModuleBranch == "main" ? GitVersion.MajorMinorPatch : GitVersion.AssemblySemVer)
                .SetFileVersion(ModuleBranch == "main" ? GitVersion.MajorMinorPatch : GitVersion.InformationalVersion));
        });

    Target SetBranch => _ => _
        .DependsOn(SetupVariables)
        .Executes(() =>
        {
            ModuleBranch = GitRepository.Branch.StartsWith("refs/") ? GitRepository.Branch.Substring(11) : GitRepository.Branch;
            Logger.Info($"Set branch name to {ModuleBranch}");
        });

    Target SetupGitHubClient => _ => _
        .OnlyWhenDynamic(() => !string.IsNullOrWhiteSpace(GithubToken))
        .DependsOn(SetBranch)
        .Executes(() => {
            Logger.Info($"We are on branch {ModuleBranch}");
            if (ModuleBranch == "main" || ModuleBranch.StartsWith("release"))
            {
                Logger.Normal("GithubToken is " + (string.IsNullOrWhiteSpace(GithubToken) ? "empty" : "present"));
                Logger.Normal($"GitRepository is: {Helpers.Dump(GitRepository)}");
                owner = GitRepository.Identifier.Split('/')[0];
                name = GitRepository.Identifier.Split('/')[1];
                gitHubClient = new GitHubClient(new ProductHeaderValue("Nuke"));
                var tokenAuth = new Credentials(GithubToken);
                gitHubClient.Credentials = tokenAuth;
            }
        });

    Target Release => _ => _
        .OnlyWhenDynamic(() => ModuleBranch == "main" || ModuleBranch.StartsWith("release"))
        .OnlyWhenDynamic(() => !string.IsNullOrWhiteSpace(GithubToken))
        .DependsOn(SetBranch)
        .DependsOn(SetupGitHubClient)
        .DependsOn(GenerateReleaseNotes)
        .DependsOn(TagRelease)
        .DependsOn(Package)
        .Executes(() => {
            var newRelease = new NewRelease(ModuleBranch == "main" ? $"v{GitVersion.MajorMinorPatch}" : $"v{GitVersion.SemVer}")
            {
                Body = releaseNotes,
                Draft = true,
                Name = ModuleBranch == "main" ? $"v{GitVersion.MajorMinorPatch}" : $"v{GitVersion.SemVer}",
                TargetCommitish = GitVersion.Sha,
                Prerelease = ModuleBranch.StartsWith("release")
            };
            Logger.Normal($"newRelease is : {Helpers.Dump(newRelease)}");
            release = gitHubClient.Repository.Release.Create(owner, name, newRelease).Result;
            Logger.Info($"{release.Name} released !");

            var artifactFile = GlobFiles(ArtifactsDirectory, "**/*.zip").FirstOrDefault();
            var artifact = File.OpenRead(artifactFile);
            var artifactInfo = new FileInfo(artifactFile);
            var assetUpload = new ReleaseAssetUpload()
            {
                FileName = artifactInfo.Name,
                ContentType = "application/zip",
                RawData = artifact
            };
            var asset = gitHubClient.Repository.Release.UploadAsset(release, assetUpload).Result;
            Logger.Info($"Asset {asset.Name} published at {asset.BrowserDownloadUrl}");
        });

    Target LogInfo => _ => _
        .Before(Release)
        .DependsOn(TagRelease)
        .DependsOn(SetBranch)
        .Executes(() =>
        {
            Logger.Info($"Original branch name is {GitRepository.Branch}");
            Logger.Info($"We are on branch {ModuleBranch} and IsOnMasterBranch is {GitRepository.IsOnMasterBranch()} and the version will be {GitVersion.SemVer}");
            using (var group = Logger.Block("GitVersion"))
            {
                Logger.Info(SerializationTasks.JsonSerialize(GitVersion));
            }
        });

    Target GenerateReleaseNotes => _ => _
        .OnlyWhenDynamic(() => ModuleBranch == "main" || ModuleBranch.StartsWith("release"))
        .OnlyWhenDynamic(() => !string.IsNullOrWhiteSpace(GithubToken))
        .DependsOn(SetupGitHubClient)
        .DependsOn(TagRelease)
        .DependsOn(SetBranch)
        .Executes(() => {
            // Get the milestone
            var milestone = gitHubClient.Issue.Milestone.GetAllForRepository(owner, name).Result.Where(m => m.Title == GitVersion.MajorMinorPatch).FirstOrDefault();
            if (milestone == null)
            {
                Logger.Warn("Milestone not found for this version");
                releaseNotes = "No release notes for this version.";
                return;
            }

            // Get the PRs
            var prRequest = new PullRequestRequest()
            {
                State = ItemStateFilter.All
            };
            var pullRequests = gitHubClient.Repository.PullRequest.GetAllForRepository(owner, name, prRequest).Result.Where(p =>
                p.Milestone?.Title == milestone.Title &&
                p.Merged == true &&
                p.Milestone?.Title == GitVersion.MajorMinorPatch);

            // Build release notes
            var releaseNotesBuilder = new StringBuilder();
            releaseNotesBuilder.AppendLine($"# {name} {milestone.Title}")
                .AppendLine("")
                .AppendLine($"A total of {pullRequests.Count()} pull requests where merged in this release.").AppendLine();

            foreach (var group in pullRequests.GroupBy(p => p.Labels[0]?.Name, (label, prs) => new { label, prs }))
            {
                releaseNotesBuilder.AppendLine($"## {group.label}");
                foreach (var pr in group.prs)
                {
                    releaseNotesBuilder.AppendLine($"- #{pr.Number} {pr.Title}. Thanks @{pr.User.Login}");
                }
            }
            releaseNotes = releaseNotesBuilder.ToString();
            using (Logger.Block("Release Notes"))
            {
                Logger.Info(releaseNotes);
            }
        });

    /// <summary>
    /// Packages the module for release.
    /// </summary>
    Target Package => _ => _
        .DependsOn(Clean)
        .DependsOn(SetManifestVersions)
        .DependsOn(Compile)
        .DependsOn(SetBranch)
        .DependsOn(TagRelease)
        .Produces(ArtifactsDirectory / "*.zip")
        .Executes(() =>
        {
            var configuration = IsLocalBuild ? "Debug" : "Release";
            Logger.Normal("IsLocalBuild: {0}", IsLocalBuild);
            Logger.Normal("Configuration: {0}", Configuration);
            Logger.Normal("configuration: {0}", configuration);
            EnsureCleanDirectory(StagingDirectory);
            Compress(RootDirectory, StagingDirectory / "Resources.zip", f =>
                f.Extension == ".ascx" ||
                f.Extension == ".resx" ||
                f.Extension == ".js" ||
                f.Extension == ".png" ||
                f.Extension == ".css" ||
                f.Directory.ToString().Contains("Templates"));

            var symbolFiles = GlobFiles(RootDirectory / "bin" / configuration, $"{ModuleName}.pdb");
            Logger.Normal("Symbol Files: " + Helpers.Dump(symbolFiles));
            Helpers.AddFilesToZip(StagingDirectory / "Symbols.zip", symbolFiles);

            Logger.Normal(InstallFiles);
            InstallFiles.ForEach(i => CopyFileToDirectory(i, StagingDirectory));

            var binaryFiles = GlobFiles(RootDirectory / "bin" / configuration, $"{ModuleName}.dll");
            Logger.Normal("Binary Files: " + Helpers.Dump(binaryFiles));
            binaryFiles.ForEach(b => CopyFileToDirectory(b, StagingDirectory / "bin"));
            var versionString = ModuleBranch == "main" ? GitVersion.MajorMinorPatch : GitVersion.SemVer;
            ZipFile.CreateFromDirectory(StagingDirectory, ArtifactsDirectory / $"{ModuleName}_{versionString}_install.zip");
            DeleteDirectory(StagingDirectory);
            if (IsWin && IsInDesktopModules)
            {
                var installFile = GlobFiles(ArtifactsDirectory, "*.zip").FirstOrDefault();
                var previousFiles = GlobFiles(DnnModuleInstallDirectory, $"{ModuleName}*.*");
                previousFiles.ForEach(f => DeleteFile(f));
                CopyFileToDirectory(installFile, DnnModuleInstallDirectory);
            }
        });

    Target Deploy => _ => _
        .OnlyWhenDynamic(() => IsInDesktopModules)
        .DependsOn(Compile)
        .Executes(() =>
        {
            var excludedFolders = new string[]
            {
                ".git",
                ".tmp",
                ".vs",
                "Artifacts",
                "bin",
                "build",
                "Components",
                "MigrationBackup",
                "obj",
                "Properties",
            };

            var excludedExtensions = new string[]
            {
                ".gitignore",
                ".nuke",
                ".config",
                ".cmd",
                ".cs",
                ".ps1",
                ".sh",
                ".csproj",
                ".user",
                ".sln",
                ".dnn",
                ".md",
                ".json",
            };

            CopyDirectoryRecursively(
                source: RootDirectory,
                target: DeployDirectory,
                directoryPolicy: DirectoryExistsPolicy.Merge,
                filePolicy: FileExistsPolicy.OverwriteIfNewer,
                excludeDirectory: d => excludedFolders.Contains(d.Name),
                excludeFile: f => excludedExtensions.Contains(f.Extension));
        });

    Target CI => _ => _
        .DependsOn(LogInfo)
        .DependsOn(Package)
        .DependsOn(GenerateReleaseNotes)
        .DependsOn(TagRelease)
        .DependsOn(Release)
        .Executes(() =>
        {

        });
}
