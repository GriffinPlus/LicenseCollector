# License Collector

[![Build Status](https://dev.azure.com/griffinplus/License%20Collector/_apis/build/status/Continuous%20Integration?branchName=master)](https://dev.azure.com/griffinplus/License%20Collector/_build/latest?definitionId=25&branchName=master)
[![Release](https://img.shields.io/github/release/griffinplus/LicenseCollector.svg?logo=github&label=Release)](https://github.com/GriffinPlus/LicenseCollector/releases)

-----

## Status

**This project is under active development and should not be used in production, yet.**

-----

## Overview and Motivation

This repository contains the Griffin+ *LicenseCollector*, a tool which helps to collect
 licenses of used 3rd party libraries in a Visual Studio solution.

The tool is designed to accept a solution file with given build configuration. It collects
 all projects that are build under this configuration. It examines all referenced NuGet
 packages for C# and C++ projects and extract their license information. In addition the
 tool supports static licenses which are found in the project folders via a given pattern.
 At last all collected license information are copied together and can be shipped with your
 software. 