﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

using GriffinPlus.Lib.Logging;
using GriffinPlus.Lib.Threading;
using GriffinPlus.LicenseCollector.Razor;

using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;

using Newtonsoft.Json.Linq;

namespace GriffinPlus.LicenseCollector
{

	/// <summary>
	/// The application's core logic.
	/// </summary>
	public class AppCore
	{
		private static readonly LogWriter           sLog = LogWriter.Get<AppCore>();
		private static readonly RazorEngineCompiler sRazorEngineCompiler;
		private static readonly bool                sDebugRazor = false;

		#region Internal members for input

		/// <summary>
		/// Solution file to process.
		/// </summary>
		private readonly string mSolutionPath;

		/// <summary>
		/// Configuration of solution to consider.
		/// </summary>
		private readonly string mConfiguration;

		/// <summary>
		/// Platform of solution to consider.
		/// </summary>
		private readonly string mPlatform;

		/// <summary>
		/// Output path to generate 3rd party notices.
		/// </summary>
		private readonly string mOutputPath;

		/// <summary>
		/// Search pattern for static licenses to include.
		/// </summary>
		private readonly string mSearchPattern;

		/// <summary>
		/// Path to the license template.
		/// </summary>
		private readonly string mLicenseTemplatePath;

		#endregion

		#region Internal members for processing

		/// <summary>
		/// Determines if processing is already completed because nothing needs to be done.
		/// </summary>
		private bool mFinishProcessing;

		/// <summary>
		/// Contains msbuild projects to process by this application.
		/// </summary>
		private readonly List<ProjectInfo> mProjectsToProcess;

		/// <summary>
		/// Contains 'id/version' of NuGet package as key and the path to the corresponding .nuspec file as value.
		/// </summary>
		private readonly Dictionary<string, string> mNuGetPackages;

		/// <summary>
		/// Contains license infos for included packages.
		/// </summary>
		private readonly List<PackageLicenseInfo> mLicenses;

		#endregion

		private const string ProjectAssets        = "project.assets.json";
		private const string PackagesConfig       = "packages.config";
		private const string PackagesFolder       = "packages";
		private const string DeprecatedLicenseUrl = "https://aka.ms/deprecateLicenseUrl";

		/// <summary>
		/// Initializes the <see cref="AppCore"/> class.
		/// </summary>
		static AppCore()
		{
			sRazorEngineCompiler = new RazorEngineCompiler();
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="AppCore"/> class.
		/// </summary>
		/// <param name="solution">Path to the solution to examine.</param>
		/// <param name="config">Solution configuration to examine.</param>
		/// <param name="platform">Solution platform to examine.</param>
		/// <param name="outputPath">Path to output third party license file.</param>
		/// <param name="searchPattern">Search pattern for static licenses.</param>
		/// <param name="licenseTemplatePath">Path to the license template.</param>
		public AppCore(
			string solution,
			string config,
			string platform,
			string outputPath,
			string searchPattern,
			string licenseTemplatePath)
		{
			// register default msbuild version to use.
			MSBuildLocator.RegisterDefaults();

			mSolutionPath = solution;
			mConfiguration = config;
			mPlatform = platform;
			mOutputPath = outputPath;
			mSearchPattern = searchPattern;
			mLicenseTemplatePath = licenseTemplatePath;

			mFinishProcessing = false;
			mProjectsToProcess = [];
			mNuGetPackages = new Dictionary<string, string>();
			mLicenses = [];
		}

		#region Collect projects under given 'configuration|platform'

		/// <summary>
		/// Examines given solution file to collect all projects that are build under given 'configuration|platform'.
		/// This method retrieves their names, project file location and base intermediate output path.
		/// </summary>
		public void CollectProjects()
		{
			var solution = SolutionFile.Parse(mSolutionPath);

			if (solution == null)
			{
				throw new ArgumentException($"The given path '{mSolutionPath}' does not define a visual studio solution.");
			}

			if (solution.SolutionConfigurations == null ||
			    !solution.SolutionConfigurations.Any(x => x.ConfigurationName.Equals(mConfiguration) && x.PlatformName.Equals(mPlatform)))
			{
				throw new ArgumentOutOfRangeException($"The given parameter set '{mConfiguration}|{mPlatform}' does not match an existing solution configuration.");
			}

			if (solution.ProjectsInOrder == null || solution.ProjectsInOrder.Count == 0)
			{
				sLog.Write(LogLevel.Notice, "The given solution file does not contain any projects.");
				mFinishProcessing = true;
				return;
			}

			foreach (var project in solution.ProjectsInOrder)
			{
				// only c# and c++ projects get processed which are build with given "configuration|platform"
				string absolutePathExtension = Path.GetExtension(project.AbsolutePath);
				string absolutePathDirectoryName = Path.GetDirectoryName(project.AbsolutePath) ?? "";
				bool isProjectSupported = absolutePathExtension is ".csproj" or ".vcxproj" &&
				                          project.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat;
				if (!isProjectSupported) continue;
				bool isConfigurationBuild = project.ProjectConfigurations.Keys.Any(
					x =>
						x.Equals($"{mConfiguration}|{mPlatform}") && project.ProjectConfigurations[x].IncludeInBuild);
				if (!isConfigurationBuild) continue;

				string packagesConfigPath = Path.Combine(absolutePathDirectoryName, PackagesConfig);
				if (File.Exists(packagesConfigPath))
				{
					// project uses packages.config
					sLog.Write(LogLevel.Debug, "Found '{0}' for '{1}'.", packagesConfigPath, project.ProjectName);
					mProjectsToProcess.Add(
						new ProjectInfo(
							project.ProjectName,
							project.AbsolutePath,
							packagesConfigPath,
							NuGetPackageDependency.PackagesConfig));
					continue;
				}

				if (absolutePathExtension.Equals(".vcxproj"))
				{
					// c++ project is included in build but has no valid NuGet dependencies
					sLog.Write(LogLevel.Debug, "Found no valid NuGet dependencies for '{0}'", project.ProjectName);
					mProjectsToProcess.Add(new ProjectInfo(project.ProjectName, project.AbsolutePath, "", NuGetPackageDependency.NoDependencies));
					continue;
				}

				// project seems to use package reference
				sLog.Write(LogLevel.Debug, "Calculate path to '{0}' for '{1}'.", ProjectAssets, project.ProjectName);
				var msBuildProject = new Project(project.AbsolutePath);
				string baseIntermediateOutputPath = msBuildProject.GetPropertyValue("BaseIntermediateOutputPath");
				if (baseIntermediateOutputPath == null)
				{
					sLog.Write(LogLevel.Error, "The project '{0}' does not define a 'BaseIntermediateOutputPath'", project.ProjectName);
					mProjectsToProcess.Add(
						new ProjectInfo(
							project.ProjectName,
							project.AbsolutePath,
							"",
							NuGetPackageDependency.NoDependencies));
					continue;
				}

				sLog.Write(LogLevel.Debug, "Found BaseIntermediateOutputPath = '{0}'", baseIntermediateOutputPath);
				// calculate path to 'project.assets.json' for given project
				string projectAssetsPath = Path.Combine(baseIntermediateOutputPath, ProjectAssets);
				if (!Path.IsPathRooted(projectAssetsPath))
				{
					// try 'BaseIntermediateOutputPath' is relative to solution
					projectAssetsPath = Path.Combine(
						Path.GetDirectoryName(mSolutionPath) ?? "",
						baseIntermediateOutputPath,
						ProjectAssets);
					if (!File.Exists(projectAssetsPath))
						// 'BaseIntermediateOutputPath' is relative to project
					{
						projectAssetsPath = Path.Combine(
							absolutePathDirectoryName,
							baseIntermediateOutputPath,
							ProjectAssets);
					}
				}

				if (!File.Exists(projectAssetsPath))
				{
					// project does not use 'project.assets.json' or no NuGet package is present
					sLog.Write(
						LogLevel.Error,
						"No '{0}' found for '{1}'.",
						ProjectAssets,
						project.ProjectName);
					mProjectsToProcess.Add(
						new ProjectInfo(
							project.ProjectName,
							project.AbsolutePath,
							"",
							NuGetPackageDependency.NoDependencies));
					continue;
				}

				mProjectsToProcess.Add(
					new ProjectInfo(
						project.ProjectName,
						project.AbsolutePath,
						projectAssetsPath,
						NuGetPackageDependency.PackageReference));
				sLog.Write(LogLevel.Debug, "Found '{0}' for '{1}'.", projectAssetsPath, project.ProjectName);
			}

			sLog.Write(LogLevel.Notice, "Successful collect all projects for solution.");
			sLog.Write(LogLevel.Notice, "--------------------------------------------------------------------------------");
		}

		#endregion

		#region Retrieve used NuGet packages

		/// <summary>
		/// Analyze each given project and get all used packages and local feeds.
		/// </summary>
		public void GetNuGetPackages()
		{
			if (mFinishProcessing)
				// already finished processing
				return;

			if (mProjectsToProcess == null || mProjectsToProcess.Count == 0)
			{
				// no project to process.
				sLog.Write(LogLevel.Notice, "There are no projects to process.");
				mFinishProcessing = true;
				return;
			}

			//search for "packages" folder if any 'packages.config' is included
			var foundPackagesFolders = new HashSet<string>();
			if (mProjectsToProcess.Any(x => x.Type == NuGetPackageDependency.PackagesConfig))
			{
				sLog.Write(LogLevel.Debug, "A 'packages.config' is included try to find 'packages' folder...");

				string solutionPackagesFolder = Path.Combine(Path.GetDirectoryName(mSolutionPath) ?? "", PackagesFolder);
				if (Directory.Exists(solutionPackagesFolder))
				{
					// 'packages' folder exist in solution directory
					sLog.Write(LogLevel.Debug, "Under solution directory: Found '{0}'", solutionPackagesFolder);
					foundPackagesFolders.Add(solutionPackagesFolder);
				}

				foreach (var project in mProjectsToProcess)
				{
					if (project.Type != NuGetPackageDependency.PackagesConfig)
						continue;

					string projectPackagesFolder = Path.Combine(
						Path.GetDirectoryName(project.ProjectAbsolutePath) ?? "",
						PackagesFolder);
					if (!Directory.Exists(projectPackagesFolder)) continue;
					// 'packages' folder exist in project directory
					sLog.Write(LogLevel.Debug, "Under project directory: Found '{0}'", projectPackagesFolder);
					foundPackagesFolders.Add(projectPackagesFolder);
				}

				if (foundPackagesFolders.Count == 0)
				{
					sLog.Write(
						LogLevel.Error,
						"No 'packages' folder found for given solution and projects. Cannot access NuGet information for 'packages.config' based projects.");
				}
			}

			// process each project depending on their type
			foreach (var project in mProjectsToProcess)
			{
				sLog.Write(
					LogLevel.Debug,
					"Determine NuGet packages for '{0}' from '{1}'.",
					project.ProjectName,
					project.NuGetInformationPath);
				switch (project.Type)
				{
					case NuGetPackageDependency.PackageReference:
					{
						GetNuGetPackageFromProjectAssets(project);
						break;
					}

					case NuGetPackageDependency.PackagesConfig:
					{
						if (foundPackagesFolders.Count > 0)
							GetNuGetPackagesFromPackagesConfig(project, foundPackagesFolders);
						break;
					}

					case NuGetPackageDependency.NoDependencies:
						continue;

					default:
						throw new FormatException($"The project type of '{project.ProjectAbsolutePath}' is not supported.");
				}
			}

			sLog.Write(LogLevel.Notice, "Collecting NuGet packages for solution completed successfully.");
			sLog.Write(LogLevel.Notice, "--------------------------------------------------------------------------------");
		}

		/// <summary>
		/// Determines NuGet packages from 'project.assets.json'.
		/// </summary>
		/// <param name="project">Project to inspect.</param>
		private void GetNuGetPackageFromProjectAssets(ProjectInfo project)
		{
			// parse 'project.assets.json' file of project to get all included NuGet packages
			var packageNuSpecs = new List<string>();
			var packageFolders = new List<string>();
			using (var reader = new StreamReader(project.NuGetInformationPath))
			{
				string json = reader.ReadToEnd();
				var jsonObject = JObject.Parse(json);

				if (jsonObject == null)
					throw new ArgumentException($"The project '{project.ProjectName}' does not contain a valid 'project.assets.json' file.");

				foreach (var property in jsonObject.Properties())
				{
					switch (property.Path)
					{
						case "libraries":
							// collect all NuGet libraries used by this project
							foreach (JProperty library in property.Value)
							{
								// collect only packages and not project references
								var type = (library.Value["type"] as JValue).Value as string;
								if (type != "package") continue;

								string packageId = library.Name.Replace('/', '\\');
								var files = library.Value["files"] as JArray;
								var nuSpec = files.First(x => ((x as JValue).Value as string).Contains(".nuspec"))
									.Value<string>();
								if (nuSpec == null)
								{
									throw new ArgumentException($"The NuGet package '{packageId}' does not contain a *.nuspec file.");
								}

								packageNuSpecs.Add(Path.Combine(packageId, nuSpec));
							}

							break;

						case "packageFolders":
							// collect all package folders used by this project
							foreach (var packageFolder in property.Value)
							{
								packageFolders.Add((packageFolder as JProperty).Name);
							}

							break;

						default:
							continue;
					}
				}
			}

			// combine nuspec and package folder to have absolute paths and store the result
			foreach (string package in packageNuSpecs)
			{
				string packageId = package.Split('\\')[0];
				if (mNuGetPackages.ContainsKey(packageId))
					// NuGet package dependency was already found within another project
					continue;

				var isNuSpecPathExisting = false;
				foreach (string feed in packageFolders)
				{
					string nuSpecFilePath = Path.Combine(feed, package);
					if (!File.Exists(nuSpecFilePath)) continue;

					mNuGetPackages.Add(packageId, nuSpecFilePath);
					sLog.Write(LogLevel.Debug, "Adding NuGet package '{0}' to processing...", packageId);
					isNuSpecPathExisting = true;
					break;
				}

				if (!isNuSpecPathExisting)
					sLog.Write(LogLevel.Error, "The NuGet package '{0}' does not have an existing '*.nuspec' file.", packageId);
			}
		}

		/// <summary>
		/// Determines NuGet packages from 'packages.config'.
		/// </summary>
		/// <param name="project">Project to inspect.</param>
		/// <param name="packagesFolders">Possible 'packages' folders inside solution or project directories.</param>
		private void GetNuGetPackagesFromPackagesConfig(ProjectInfo project, HashSet<string> packagesFolders)
		{
			sLog.Write(LogLevel.Debug, "Getting nuspec files for '{0}'...", project.ProjectName);

			var packagePaths = new Dictionary<string, string>();
			// extract 'packages.config' and determine id and version
			foreach (var element in XElement.Load(project.NuGetInformationPath).Descendants())
			{
				switch (element.Name.LocalName)
				{
					case "package":
						string id = element.Attribute("id")?.Value;
						if (string.IsNullOrEmpty(id))
							continue;
						string version = element.Attribute("version")?.Value;
						if (string.IsNullOrEmpty(version))
							continue;
						packagePaths.Add(id, $"{id}.{version}");
						break;
				}
			}

			var nugetPackages = new Dictionary<string, string>();
			// find path to NuGet packages inside one of the packages folder
			foreach (string id in packagePaths.Keys)
			{
				foreach (string packagesFolder in packagesFolders)
				{
					string nugetPackagePath = Path.Combine(packagesFolder, packagePaths[id], $"{packagePaths[id]}.nupkg");
					if (!File.Exists(nugetPackagePath))
					{
						continue;
					}

					sLog.Write(LogLevel.Debug, "{0}: Found NuGet package '{1}'.", project.ProjectName, nugetPackagePath);
					nugetPackages.Add(id, nugetPackagePath);
					break;
				}
			}

			// extract 'nuspec' file from package and store information
			foreach (string nugetId in nugetPackages.Keys)
			{
				if (mNuGetPackages.ContainsKey(nugetId))
					// NuGet package dependency was already found within another project
					continue;

				// extract nuspec file from package
				using (var fs = new FileStream(nugetPackages[nugetId], FileMode.Open))
				{
					var archive = new ZipArchive(fs, ZipArchiveMode.Read);
					var nuSpec = archive.Entries.FirstOrDefault(x => x.Name.EndsWith(".nuspec"));
					if (nuSpec == null)
					{
						sLog.Write(LogLevel.Error, "The NuGet package '{0}' does not contain a '.nuspec' file.", nugetId);
						continue;
					}

					string nuspecPath = Path.Combine(Path.GetDirectoryName(nugetPackages[nugetId]) ?? "", $"{nugetId}.nuspec");
					using (var nuspecStream = new FileStream(nuspecPath, FileMode.Create, FileAccess.Write))
					{
						nuSpec.Open().CopyTo(nuspecStream);
					}

					sLog.Write(LogLevel.Debug, "Adding NuGet package '{0}' to processing...", nugetId);
					mNuGetPackages.Add(nugetId, nuspecPath);
				}
			}
		}

		#endregion

		#region Get license information from NuGet packages

		/// <summary>
		/// Inspect each given nuget package for either license or licenseUrl tags. Download license if necessary.
		/// </summary>
		public void GetNuGetLicenseInfo()
		{
			if (mFinishProcessing)
				// already finished processing
				return;

			if (mNuGetPackages == null || mNuGetPackages.Count == 0)
			{
				// no NuGet packages found to process.
				sLog.Write(LogLevel.Notice, "There are no NuGet packages found.");
				return;
			}

			foreach (string nuSpecFilePath in mNuGetPackages.Values)
			{
				sLog.Write(LogLevel.Debug, "Retrieving NuGet specification information from '{0}'...", nuSpecFilePath);

				// extract important information from NuGet specification file
				var doc = new XmlDocument();
				doc.Load(nuSpecFilePath);
				XmlNode root = doc.DocumentElement;
				if (root == null)
					continue;

				var namespaceManager = new XmlNamespaceManager(doc.NameTable);
				namespaceManager.AddNamespace("nu", root.NamespaceURI);

				string identifier =
					root.SelectSingleNode("/nu:package/nu:metadata/nu:id", namespaceManager)?.InnerText ??
					string.Empty;
				string version =
					root.SelectSingleNode("/nu:package/nu:metadata/nu:version", namespaceManager)?.InnerText ??
					string.Empty;
				string authors =
					root.SelectSingleNode("/nu:package/nu:metadata/nu:authors", namespaceManager)?.InnerText ??
					string.Empty;
				string licenseUrl =
					root.SelectSingleNode("/nu:package/nu:metadata/nu:licenseUrl", namespaceManager)?.InnerText ??
					string.Empty;
				if (licenseUrl == DeprecatedLicenseUrl)
					licenseUrl = string.Empty;
				string projectUrl =
					root.SelectSingleNode("/nu:package/nu:metadata/nu:projectUrl", namespaceManager)?.InnerText ??
					string.Empty;
				string copyright =
					root.SelectSingleNode("/nu:package/nu:metadata/nu:copyright", namespaceManager)?.InnerText ??
					string.Empty;
				var licenseNode = root.SelectSingleNode("/nu:package/nu:metadata/nu:license", namespaceManager);
				var license = string.Empty;
				if (licenseNode != null)
				{
					switch (licenseNode.Attributes?["type"]?.Value)
					{
						// TODO: Is there a better handling e.g. loading templates?
						case "expression":
							// SPDX expression as defined in https://spdx.org/spdx-specification-21-web-version#h.jxpfx0ykyb60
							license = licenseNode.InnerText ?? string.Empty;
							break;

						case "file":
							// license is contained as file within the nuget package
							string directoryPath = Path.GetDirectoryName(nuSpecFilePath) ?? "";
							string licensePath = Path.Combine(directoryPath, licenseNode.InnerText);
							if (!File.Exists(licensePath))
							{
								// License file does not exist, try to open NuGet package on same folder hierarchy
								// this could happen if a packages.config was processed and only the nuspec was unzipped.
								string nugetPackage = Path.Combine(directoryPath, $"{identifier}.{version}.nupkg");
								if (!File.Exists(nugetPackage))
								{
									sLog.Write(
										LogLevel.Error,
										"The path '{0}' to the license defined by the NuGet specification does not exists.",
										licensePath);
									break;
								}

								// try to extract license file from package
								using (var fs = new FileStream(nugetPackage, FileMode.Open))
								{
									var archive = new ZipArchive(fs, ZipArchiveMode.Read);
									var licenseEntry = archive.Entries.FirstOrDefault(x => x.Name.Contains(licenseNode.InnerText));
									if (licenseEntry == null)
									{
										sLog.Write(LogLevel.Error, "The NuGet package '{0}' does not contain '{1}'", licenseNode.InnerText);
										break;
									}

									using (var licenseStream = new FileStream(licensePath, FileMode.Create, FileAccess.Write))
									{
										licenseEntry.Open().CopyTo(licenseStream);
									}
								}
							}

							license = File.ReadAllText(licensePath) ?? string.Empty;
							break;
					}
				}

				// TODO: extend to download other url then from github.com
				if (string.IsNullOrEmpty(license) && !string.IsNullOrEmpty(licenseUrl) &&
				    (licenseUrl.Contains("github.com") || licenseUrl.Contains("/raw.githubusercontent.com/")))
				{
					// download license when only url to license is given
					string url = licenseUrl.Contains("github.com") ? licenseUrl.Replace("/blob/", "/raw/") : licenseUrl;
					try
					{
						using var client = new HttpClient();
						license = client.GetStringAsync(url).WaitAndUnwrapException();
						sLog.Write(LogLevel.Debug, "Successfully downloaded license '{0}' for '{1}'", url, identifier);
					}
					catch (WebException ex)
					{
						sLog.Write(LogLevel.Error, "Error during downloading '{0}': {1}", url, ex.Message);
					}
				}

				if (string.IsNullOrEmpty(license) && string.IsNullOrEmpty(licenseUrl))
				{
					sLog.Write(LogLevel.Error, "The NuGet specification file '{0}' does not contain valid license information", nuSpecFilePath);
					continue;
				}

				// condition strings
				identifier = identifier?.Trim();
				version = version?.Trim();
				authors = authors?.Trim();
				copyright = copyright?.Trim();
				licenseUrl = licenseUrl?.Trim();
				projectUrl = projectUrl?.Trim();
				license = license?.Trim();

				var package = new PackageLicenseInfo(identifier, version, authors, copyright, licenseUrl, projectUrl, license);
				mLicenses.Add(package);
				sLog.Write(LogLevel.Debug, "Successfully extracted license information for '{0} v{1}'.", identifier, version);
			}

			sLog.Write(LogLevel.Notice, "Successfully extracted license information from found NuGet packages.");
			sLog.Write(LogLevel.Notice, "--------------------------------------------------------------------------------");
		}

		#endregion

		#region Get static license information project folder

		/// <summary>
		/// Inspect given project folders for static licenses and load them.
		/// </summary>
		public void GetStaticLicenseInfo()
		{
			if (mFinishProcessing)
				// already finished processing
				return;

			foreach (var project in mProjectsToProcess)
			{
				string projectDir = Path.GetDirectoryName(project.ProjectAbsolutePath) ?? "";

				IEnumerable<string> staticProjectLicenses = Directory
					.EnumerateFiles(projectDir, mSearchPattern, SearchOption.AllDirectories)
					.ToArray();

				if (staticProjectLicenses.Any())
				{
					foreach (string staticLicensePath in staticProjectLicenses)
					{
						sLog.Write(LogLevel.Debug, "Project '{0}': Found static license '{1}'", project.ProjectName, staticLicensePath);
						string staticLicenseIdentifier = Path.GetFileNameWithoutExtension(staticLicensePath);
						string license = File.ReadAllText(staticLicensePath);
						if (mLicenses.Any(x => x.PackageIdentifier == staticLicenseIdentifier))
						{
							// static license is already existing in another project
							sLog.Write(LogLevel.Debug, "Static license '{0}' was already found in another project.", staticLicenseIdentifier);
							continue;
						}

						mLicenses.Add(new PackageLicenseInfo(staticLicenseIdentifier, license));
					}
				}
				else
				{
					sLog.Write(LogLevel.Debug, "No static licenses found under project directory '{0}'", projectDir);
				}
			}

			sLog.Write(LogLevel.Notice, "Scanning project directories for static licenses completed successfully.");
			sLog.Write(LogLevel.Notice, "--------------------------------------------------------------------------------");
		}

		#endregion

		#region Generate output file

		/// <summary>
		/// Generate output third party notice with collected license information.
		/// </summary>
		public async Task GenerateOutputFileAsync()
		{
			if (mLicenses == null || mLicenses.Count == 0 || mFinishProcessing)
			{
				sLog.Write(LogLevel.Notice, "Nothing to store, because there are no licenses found.");
				return;
			}

			// overwrite existing file if necessary
			if (File.Exists(mOutputPath))
				File.Delete(mOutputPath);

			// render the template
			var model = new { Licenses = mLicenses };
			string patchedTemplate = RunTemplate(mLicenseTemplatePath, model);

			// save the rendered template to the specified file
			await File.WriteAllTextAsync(mOutputPath, patchedTemplate);

			sLog.Write(LogLevel.Notice, "Collected licenses have been successfully written to '{0}'", mOutputPath);
			sLog.Write(LogLevel.Notice, "--------------------------------------------------------------------------------");
		}

		/// <summary>
		/// Renders the specified template file.
		/// </summary>
		/// <param name="path">Path of the template file to render.</param>
		/// <param name="context">Context object containing data to use within the template.</param>
		/// <returns>The rendered template.</returns>
		public string RunTemplate(string path, object context)
		{
			if (!sRazorEngineCompiler.IsTemplateCached(path))
			{
				using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
				using var reader = new StreamReader(fs, true);
				string template = reader.ReadToEnd();
				using (TimingLogger.Measure(sLog, LogLevel.Timing, $"Compiling template ({path})"))
				{
					sRazorEngineCompiler.Compile(
						path,
						template,
						new RazorEngineCompilationOptions
						{
							IncludeDebuggingInfo = sDebugRazor,
							TemplateFilename = Path.GetFileName(path)
						});
				}
			}

			string fileExtension = Path.GetExtension(path);
			Debug.Assert(fileExtension != null, nameof(fileExtension) + " != null");

			string filePathWithoutTemplateExtension = Path.GetFileNameWithoutExtension(path);
			string templateExtension = Path.GetExtension(filePathWithoutTemplateExtension).ToLower();

			bool isHtml;
			switch (templateExtension)
			{
				case ".cshtml":
				case ".html":
					isHtml = true;
					break;

				default:
					isHtml = false;
					break;
			}

			using (TimingLogger.Measure(sLog, LogLevel.Timing, $"Rendering template ({path})"))
			{
				return sRazorEngineCompiler.Run(path, isHtml, context);
			}
		}

		#endregion
	}

}
