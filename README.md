# License Collector

[![Build Status](https://dev.azure.com/griffinplus/License%20Collector/_apis/build/status/Continuous%20Integration?branchName=master)](https://dev.azure.com/griffinplus/License%20Collector/_build/latest?definitionId=25&branchName=master)
[![Release](https://img.shields.io/github/release/griffinplus/LicenseCollector.svg?logo=github&label=Release)](https://github.com/GriffinPlus/LicenseCollector/releases)

-----

## Overview and Motivation

This repository contains the Griffin+ *LicenseCollector*, a tool which helps to collect licenses of used 3rd party libraries in a Visual Studio solution.

The tool is designed to accept a solution file with a specific build configuration. It collects all projects that are built under this configuration. It examines all referenced NuGet packages for C# and C++ projects and extracts their license information. In addition the tool supports static licenses which are found in the project folders via a given pattern as well. At last all collected license information are copied together and can be shipped with your software.

## Requirements

The solution file is read without MSBuild. To locate the `project.assets.json` of projects using `PackageReference`, the tool evaluates these projects by running `dotnet msbuild -getProperty` in a separate process. Therefore the .NET SDK 8.0.100 or later must be installed and `dotnet` must be in the `PATH`. The SDK is selected the same way as for a build of the solution, i.e. a `global.json` in the solution directory or one of its parent directories is respected.

The solution must have been restored before running the tool.

Projects using `PackageReference` with the MSBuild property `IsTestProject` set to `true` (set by `Microsoft.NET.Test.Sdk`, `MSTest.Sdk`, xUnit v3 etc.) are skipped, because their dependencies are not shipped.

## Razor template for output file

An optional parameter can be given to load a razor template file. The template defines how the output is formatted to ensure optimal flexibility when generating a *Third Party Notices* file. An example for a template can be found in this repository (*THIRD_PARTY_NOTICES.template*).

You can write a razor template with `Model.Licenses` containing a `List<PackageLicenseInfo>` object. The template does not support HTML encoding because the extracted licenses contain plain text and escape sequences.

The `PackageLicenseInfo` class consists of the following properties that can be used within a template:

```csharp
public string PackageIdentifier { get; }
public string PackageVersion { get; }
public string Author { get; }
public string Copyright { get; }
public string ProjectUrl { get; }
public string LicenseUrl { get; }
public string License { get; }
```

## CI Versioning

The Azure pipeline pins GitVersion.Tool to 7.0.0 and uses `GitVersion.CI.yml` with the full Git history and tags. Version outputs use the GitVersion 7 Azure Pipelines naming convention, for example `$(GitVersion_SemVer)`.

On `master`, Continuous Delivery with the explicit `ci` label preserves the legacy fallback-tag behavior: commits after `v7.1.0` produce `7.1.1-ci.1`, while the tagged release itself remains `7.1.0`.

The CI configuration is separate from the ignored `GitVersion.yml`, which the legacy local `Build.ps1` workflow copies from `build.vs`. When upgrading GitVersion, review its [migration guide](https://gitversion.net/7.0/docs/migration/v6-to-v7) and validate the CI configuration and version outputs before changing the pinned version.

## Current Limitations

+ Only supports C# and C++ projects within a given solution
+ Only downloads licenses with a Github URL, when a URL is defined for the NuGet package
+ Only prints the SPDX expression when given instead of a template license