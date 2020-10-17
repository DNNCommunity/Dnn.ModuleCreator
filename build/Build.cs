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

[GitHubActions(
    name: "Build",
    image: GitHubActionsImage.WindowsLatest,
    ImportGitHubTokenAs = "githubtoken",
    InvokedTargets = new[] { "CI" },
    OnPullRequestBranches = new[] { "develop", "release/**" },
    OnPushBranches = new[] { "main", "release/**" })]
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
    readonly string GithubToken;

    [Solution] readonly Solution Solution;
    [GitRepository] readonly GitRepository GitRepository;
    [GitVersion(Framework = "netcoreapp3.1", UpdateAssemblyInfo = false, NoFetch = true)] readonly GitVersion GitVersion;


    readonly string ModuleName = "Dnn.Modules.ModuleCreator";
    readonly IReadOnlyCollection<string> SymbolFiles;
    readonly IReadOnlyCollection<string> InstallFiles;
    readonly IReadOnlyCollection<string> BinaryFiles;
    readonly bool IsInDesktopModules;
    readonly AbsolutePath ArtifactsDirectory;
    readonly AbsolutePath StagingDirectory;
    readonly AbsolutePath DeployDirectory;
    readonly AbsolutePath DnnModuleInstallDirectory;
    string ModuleBranch;

    public Build()
    {
        using (var block = Logger.Block("Info"))
        {
            Logger.Normal(Configuration);
        }
        SymbolFiles = GlobFiles(RootDirectory / "bin" / Configuration, $"{ModuleName}.pdb");
        InstallFiles = GlobFiles(RootDirectory, "*.txt", "*.dnn");
        ArtifactsDirectory = RootDirectory / "Artifacts";
        StagingDirectory = ArtifactsDirectory / "Staging";
        BinaryFiles = GlobFiles(RootDirectory / "bin" / Configuration, $"{ModuleName}.dll");
        IsInDesktopModules = RootDirectory.Parent.ToString().EndsWith("DesktopModules");
        DeployDirectory = IsInDesktopModules ? RootDirectory.Parent / "Admin" / "ModuleCreator" : null;
        DnnModuleInstallDirectory = RootDirectory.Parent.Parent / "Install" / "Module";
    }

    Target Clean => _ => _
        .Before(Restore)
        .Before(Package)
        .Executes(() =>
        {
            EnsureCleanDirectory(ArtifactsDirectory);
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            var project = Solution.GetProject(ModuleName);
            DotNetRestore(s => s
                .SetProjectFile(project));
        });

    Target SetManifestVersions => _ => _
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
        .Executes(() =>
        {
            ModuleBranch = GitRepository.Branch.StartsWith("refs/") ? GitRepository.Branch.Substring(11) : GitRepository.Branch;
            Logger.Info($"Set branch name to {ModuleBranch}");
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
            EnsureCleanDirectory(StagingDirectory);
            Compress(RootDirectory, StagingDirectory / "Resources.zip", f =>
                f.Extension == ".ascx" ||
                f.Extension == ".resx" ||
                f.Extension == ".js" ||
                f.Extension == ".png" ||
                f.Extension == ".css" ||
                f.Directory.ToString().Contains("Templates"));
            Helpers.AddFilesToZip(StagingDirectory / "Symbols.zip", SymbolFiles);
            InstallFiles.ForEach(i => CopyFileToDirectory(i, StagingDirectory));
            BinaryFiles.ForEach(b => CopyFileToDirectory(b, StagingDirectory / "bin"));
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
        .DependsOn(Package)
        .DependsOn(TagRelease)
        .Executes(() =>
        {

        });
}
