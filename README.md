# License Collector

[![Build Status](https://dev.azure.com/griffinplus/License%20Collector/_apis/build/status/Continuous%20Integration?branchName=master)](https://dev.azure.com/griffinplus/License%20Collector/_build/latest?definitionId=25&branchName=master)
[![Release](https://img.shields.io/github/release/griffinplus/LicenseCollector.svg?logo=github&label=Release)](https://github.com/GriffinPlus/LicenseCollector/releases)

-----

## Overview and Motivation

This repository contains the Griffin+ *LicenseCollector*, a tool which helps to collect licenses of used 3rd party libraries in a Visual Studio solution.

The tool is designed to accept a solution file with a specific build configuration. It collects all projects that are built under this configuration. It examines all referenced NuGet packages for C# and C++ projects and extracts their license information. In addition the tool supports static licenses which are found in the project folders via a given pattern as well. At last all collected license information are copied together and can be shipped with your software.

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

## Current Limitations 

+ Only supports C# and C++ projects within a given solution
+ Only downloads licenses with a Github URL, when a URL is defined for the NuGet package
+ Only prints the SPDX expression when given instead of a template license