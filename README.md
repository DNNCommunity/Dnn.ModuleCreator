# Dnn.ModuleCreator
Dnn Module Creator is a Dnn module that allows quickly creating Dnn modules directly on a running site.

This module was extracted from https://github.com/DnnSoftware/Dnn.Platform

## Contributing
1. Fork this repository to your own github account.
2. Clone this repository into the DesktopModules folder of a working local Dnn installation using your favorite git client. If using the command-line it would look like this:
    ```
    cd c:\websites\dnn980\DesktopModules
    git clone https://github.com/your-user-name/Dnn.ModuleCreator
    ```
3. Open the solution in Visual Studio.
4. The first you will need to create a Dnn package to properly register the module in dnn by selecting the `Package` mode and clicking the debug button. This will fire up a package build in the command line for you.

    ![Package mode](.github/images/Sreenshot1.png)
5. In Dnn, go to `Extensions => Available Extensions => Modules` and install the Module Creator module
6. Make sure to create a branch to contain your changes before you start working.
7. As you develop the module you can then switch the build mode to `Deploy` to have your changes applied in place on the working website.
8. Commit your changes and push to your fork of the repository.
9. Create a pull request towards this repository.

## Releases
This information is for maintainers of this repository. This repository uses GithubActions and GitVersion for release management. Build targets are in place for CI to help with release management.
1. Make sure all pull requests for a version have a milestone set to that version
2. `develop` is the branch to merge any pull request for the next version, you don't have to decide on the version number until you are ready to release.
3. When ready to release, first create a release branch with the version number you want for this release. Ex: `release/9.8.1` Within a few minutes you will get a beta release published, it will be marked as `pre-release` and `draft`. It will collect all the PR titles and contributors to automatically generate the release notes. At this point you can customize the release notes if you want before publishing the beta release.
4. When ready for an official release, merge the release branch into the `main` branch. This will again automate the release creation but without it beeing beta (you still need to publish it as it will by default be `draft` to allow editing before publishing).