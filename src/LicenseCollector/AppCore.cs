using GriffinPlus.Lib.Logging;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using Newtonsoft.Json.Linq;

namespace GriffinPlus.LicenseCollector
{
	/// <summary>
	/// The application's core logic.
	/// </summary>
	public class AppCore
	{
		private static readonly LogWriter sLog = Log.GetWriter<AppCore>();

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

		private const string cProjectAssets = "project.assets.json";
		private const string cPackagesConfig = "packages.config";
		private const string cPackagesFolder = "packages";
		private const string cDeprecatedLicenseUrl = "https://aka.ms/deprecateLicenseUrl";

		/// <summary>
		/// Initializes a new instance of the <see cref="AppCore"/> class.
		/// </summary>
		/// <param name="solution">Path to the solution to examine.</param>
		/// <param name="config">Solution configuration to examine.</param>
		/// <param name="platform">Solution platform to examine.</param>
		/// <param name="outputPath">Path to output third party license file.</param>
		public AppCore(string solution, string config, string platform, string outputPath, string searchPattern)
		{
			// register default msbuild version to use.
			MSBuildLocator.RegisterDefaults();

			mSolutionPath = solution;
			mConfiguration = config;
			mPlatform = platform;
			mOutputPath = outputPath;
			mSearchPattern = searchPattern;

			mFinishProcessing = false;
			mProjectsToProcess = new List<ProjectInfo>();
			mNuGetPackages = new Dictionary<string, string>();
			mLicenses = new List<PackageLicenseInfo>();
		}

		#region Collect projects under given 'configuration|platform'

		/// <summary>
		/// Examines given solution file to collect all projects that are build under given 'configuration|platform'.
		/// This method retrieves their names, project file location and base intermediate output path.
		/// </summary>
		public void CollectProjects()
		{
			SolutionFile solution = SolutionFile.Parse(mSolutionPath);

			if (solution == null)
			{
				throw new ArgumentException(
					$"The given path '{mSolutionPath}' does not define a visual studio solution.");
			}

			if (solution.SolutionConfigurations == null ||
			    !solution.SolutionConfigurations.Any(
				    x => x.ConfigurationName.Equals(mConfiguration) && x.PlatformName.Equals(mPlatform)))
			{
				throw new ArgumentOutOfRangeException(
					$"The given parameter set '{mConfiguration}|{mPlatform}' does not match an existing solution configuration.");
			}

			if (solution.ProjectsInOrder == null || solution.ProjectsInOrder.Count == 0)
			{
				sLog.Write(LogLevel.Note, "The given solution file does not contain any projects.");
				mFinishProcessing = true;
				return;
			}

			foreach (ProjectInSolution project in solution.ProjectsInOrder)
			{
				// only c# and c++ projects get processed which are build with given "configuration|platform"
				bool isProjectSupported = (Path.GetExtension(project.AbsolutePath).Equals(".csproj") ||
				                           Path.GetExtension(project.AbsolutePath).Equals(".vcxproj")) &&
				                          project.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat;
				if (!isProjectSupported) continue;
				bool isConfigurationBuild = project.ProjectConfigurations.Keys.Any(x =>
					x.Equals($"{mConfiguration}|{mPlatform}") && project.ProjectConfigurations[x].IncludeInBuild);
				if (!isConfigurationBuild) continue;

				string[] packagesConfig = Directory.EnumerateFiles(Path.GetDirectoryName(project.AbsolutePath), cPackagesConfig,
					SearchOption.AllDirectories).ToArray();
				// c++ project is included in build but has no valid NuGet dependencies
				if (packagesConfig.Length != 1 && Path.GetExtension(project.AbsolutePath).Equals(".vcxproj"))
				{
					sLog.Write(LogLevel.Developer, "Found no valid NuGet dependencies for '{0}'", project.ProjectName);
					mProjectsToProcess.Add(new ProjectInfo(project.ProjectName, project.AbsolutePath, "", NuGetPackageDependency.NoDependencies));
					continue;
				}
				// project uses packages.config
				if (packagesConfig.Length == 1)
				{
					sLog.Write(LogLevel.Developer, "Found '{0}' for '{1}'.", packagesConfig[0], project.ProjectName);
					mProjectsToProcess.Add(new ProjectInfo(project.ProjectName, project.AbsolutePath,
						packagesConfig[0], NuGetPackageDependency.PackagesConfig));
					continue;
				}

				// project seems to use package reference
				sLog.Write(LogLevel.Developer, "Calculate path to '{0}' for '{1}'.", cProjectAssets, project.ProjectName);
				var msBuildProject = new Project(project.AbsolutePath);
				string baseIntermediateOutputPath = msBuildProject.GetPropertyValue("BaseIntermediateOutputPath");
				if (baseIntermediateOutputPath == null)
				{
					sLog.Write(LogLevel.Error, "The project '{0}' does not define a 'BaseIntermediateOutputPath'", project.ProjectName);
					mProjectsToProcess.Add(new ProjectInfo(project.ProjectName, project.AbsolutePath, "",
						NuGetPackageDependency.NoDependencies));
					continue;
				}
				sLog.Write(LogLevel.Developer, "Found BaseIntermediateOutputPath = '{0}'", baseIntermediateOutputPath);
				// calculate path to 'project.assets.json' for given project
				string projectAssetsPath = Path.Combine(baseIntermediateOutputPath, cProjectAssets);
				if (!Path.IsPathRooted(projectAssetsPath))
				{
					projectAssetsPath = Path.Combine(Path.GetDirectoryName(mSolutionPath),
						baseIntermediateOutputPath, cProjectAssets);
					if (!File.Exists(projectAssetsPath))
					{
						sLog.Write(LogLevel.Developer,
							"The file '{0}' does not exists for project '{1}'. Try relative from project folder...",
							projectAssetsPath, project.ProjectName);
						projectAssetsPath = Path.Combine(Path.GetDirectoryName(project.AbsolutePath),
							baseIntermediateOutputPath, cProjectAssets);
					}
				}
				// project does not use 'project.assets.json' or no NuGet package is present
				if (!File.Exists(projectAssetsPath))
				{
					sLog.Write(LogLevel.Error, "No '{0}' found for '{1}'.", cProjectAssets,
						project.ProjectName);
					mProjectsToProcess.Add(new ProjectInfo(project.ProjectName, project.AbsolutePath, "",
						NuGetPackageDependency.NoDependencies));
					continue;
				}

				mProjectsToProcess.Add(new ProjectInfo(project.ProjectName, project.AbsolutePath, projectAssetsPath,
					NuGetPackageDependency.PackageReference));
				sLog.Write(LogLevel.Developer, "Found '{0}' for '{1}'.", projectAssetsPath, project.ProjectName);
			}
			sLog.Write(LogLevel.Note, "Successful collect all projects for solution.");
			sLog.Write(LogLevel.Note, "--------------------------------------------------------------------------------");
		}

		#endregion

		#region Retrieve used NuGet packages
		/// <summary>
		/// Analyze each given project and get all used packages and local feeds.
		/// </summary>
		public void GetNuGetPackages()
		{
			// already finished processing
			if (mFinishProcessing)
				return;

			// no project to process.
			if (mProjectsToProcess == null || mProjectsToProcess.Count == 0)
			{
				sLog.Write(LogLevel.Note, "There are no projects to process.");
				mFinishProcessing = true;
				return;
			}

			//search for "packages" folder if any 'packages.config' is included
			var foundPackagesFolders = new HashSet<string>();
			if (mProjectsToProcess.Any(x => x.Type == NuGetPackageDependency.PackagesConfig))
			{
				sLog.Write(LogLevel.Developer, "A 'packages.config' is included try to find 'packages' folder...");
				// Enumerate all packages folder for solution directory
				IEnumerable<string> solutionPackagesFolders = Directory.EnumerateDirectories(Path.GetDirectoryName(mSolutionPath), cPackagesFolder, SearchOption.AllDirectories);
				if (solutionPackagesFolders != null)
				{
					foreach (string packagesFolder in solutionPackagesFolders)
					{
						sLog.Write(LogLevel.Developer, "Under solution directory: Found '{0}'", packagesFolder);
						foundPackagesFolders.Add(packagesFolder);
					}
				}
				// Enumerate all packages folder for each project directory
				foreach (ProjectInfo project in mProjectsToProcess)
				{
					if (project.Type != NuGetPackageDependency.PackagesConfig)
						continue;
					IEnumerable<string> projectPackagesFolders = Directory.EnumerateDirectories(Path.GetDirectoryName(project.ProjectAbsolutePath), cPackagesFolder,
						SearchOption.AllDirectories);
					if (projectPackagesFolders == null) continue;
					foreach (string packagesFolder in projectPackagesFolders)
					{
						sLog.Write(LogLevel.Developer, "Under project directory: Found '{0}'", packagesFolder);
						foundPackagesFolders.Add(packagesFolder);
					}
				}

				if (foundPackagesFolders.Count == 0)
				{
					sLog.Write(LogLevel.Error,
						"No 'packages' folder found for given solution and projects. Cannot access NuGet information for 'packages.config' based projects.");
				}
			}

			foreach (ProjectInfo project in mProjectsToProcess)
			{
				sLog.Write(LogLevel.Developer, "Determine NuGet packages for '{0}' from '{1}'.",
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
			sLog.Write(LogLevel.Note, "Successful collect NuGet packages for solution.");
			sLog.Write(LogLevel.Note, "--------------------------------------------------------------------------------");
		}

		/// <summary>
		/// Determines NuGet packages from 'project.assets.json'.
		/// </summary>
		/// <param name="project">Project to inspect.</param>
		private void GetNuGetPackageFromProjectAssets(ProjectInfo project)
		{
			// parse 'project.assets.json' file of project to get all included NuGet packages
			var packageNuspecs = new List<string>();
			var packageFolders = new List<string>();
			using (var reader = new StreamReader(project.NuGetInformationPath))
			{
				string json = reader.ReadToEnd();
				JObject jsonObject = JObject.Parse(json);

				if(jsonObject == null)
					throw new ArgumentException($"The project '{project.ProjectName}' does not contain a valid 'project.assets.json' file.");

				foreach (JProperty property in jsonObject.Properties())
				{
					switch (property.Path)
					{
						// collect all NuGet libraries used by this project
						case "libraries":
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
									throw new ArgumentException(
										$"The NuGet package '{packageId}' does not contain a *.nuspec file.");
								packageNuspecs.Add(Path.Combine(packageId, nuSpec));
							}
							break;
						// collect all package folders used by this project
						case "packageFolders":
							foreach (JToken packageFolder in property.Value)
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
			foreach (string package in packageNuspecs)
			{
				string packageId = package.Split('\\')[0];
				// NuGet package dependency was already found within another project
				if (mNuGetPackages.ContainsKey(packageId))
					continue;

				var isNuSpecPathExisting = false;
				foreach (string feed in packageFolders)
				{
					string nuSpecFilePath = Path.Combine(feed, package);
					if (!File.Exists(nuSpecFilePath)) continue;

					mNuGetPackages.Add(packageId, nuSpecFilePath);
					sLog.Write(LogLevel.Developer, "Add NuGet package '{0}' to processing...", packageId);
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
			sLog.Write(LogLevel.Developer, "Get nuspec files for '{0}'", project.ProjectName);

			var packagePaths = new Dictionary<string, string>();
			// extract 'packages.config' and determine id and version
			foreach (XElement element in XElement.Load(project.NuGetInformationPath).DescendantsAndSelf())
			{
				switch (element.Name.LocalName)
				{
					case "package":
						string id = element.Attribute("id").Value;
						if (string.IsNullOrEmpty(id))
							continue;
						string version = element.Attribute("version").Value;
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
					sLog.Write(LogLevel.Developer, "{0}: Found NuGet package '{1}'.", project.ProjectName, nugetPackagePath);
					nugetPackages.Add(id, nugetPackagePath);
					break;
				}
			}

			// extract 'nuspec' file from package and store information
			foreach (string nugetId in nugetPackages.Keys)
			{
				// NuGet package dependency was already found within another project
				if (mNuGetPackages.ContainsKey(nugetId))
					continue;

				// extract nuspec file from package
				using (var fs = new FileStream(nugetPackages[nugetId], FileMode.Open))
				{
					ZipArchive archive = new ZipArchive(fs, ZipArchiveMode.Read);
					ZipArchiveEntry nuSpec = archive.Entries.First(x => x.Name.Contains(".nuspec"));
					if (nuSpec == null)
					{
						sLog.Write(LogLevel.Error, "The NuGet package '{0}' does not contains an '.nuspec' file.", nugetId);
					}

					string nuspecPath = Path.Combine(Path.GetDirectoryName(nugetPackages[nugetId]), $"{nugetId}.nuspec");
					using (var nuspecStream = new FileStream(nuspecPath, FileMode.Create, FileAccess.Write))
						nuSpec.Open().CopyTo(nuspecStream);

					sLog.Write(LogLevel.Developer, "Add NuGet package '{0}' to processing...", nugetId);
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
				return;

			// no NuGet packages found to process.
			if (mNuGetPackages == null || mNuGetPackages.Count == 0)
			{
				sLog.Write(LogLevel.Note, "There are no NuGet packages found.");
				return;
			}

			foreach (string nuSpecFilePath in mNuGetPackages.Values)
			{
				sLog.Write(LogLevel.Developer, "Begin retrieving NuGet specification information from '{0}'...", nuSpecFilePath);

				// extract important information from NuGet specification file
				var doc = new XmlDocument();
				doc.Load(nuSpecFilePath);
				XmlNode root = doc.DocumentElement;
				if (root == null)
					continue;

				var namespaceManager = new XmlNamespaceManager(doc.NameTable);
				namespaceManager.AddNamespace("nu", root.NamespaceURI);

				string identifier = root.SelectSingleNode("/nu:package/nu:metadata/nu:id", namespaceManager)?.InnerText;
				string version = root.SelectSingleNode("/nu:package/nu:metadata/nu:version", namespaceManager)?.InnerText;
				string authors = root.SelectSingleNode("/nu:package/nu:metadata/nu:authors", namespaceManager)?.InnerText;
				string licenseUrl = root.SelectSingleNode("/nu:package/nu:metadata/nu:licenseUrl", namespaceManager)?.InnerText;
				if (licenseUrl == cDeprecatedLicenseUrl)
					licenseUrl = null;
				string projectUrl = root.SelectSingleNode("/nu:package/nu:metadata/nu:projectUrl", namespaceManager)?.InnerText;
				string copyright = root.SelectSingleNode("/nu:package/nu:metadata/nu:copyright", namespaceManager)?.InnerText;
				XmlNode licenseNode = root.SelectSingleNode("/nu:package/nu:metadata/nu:license", namespaceManager);
				string license = null;
				if (licenseNode != null)
				{
					switch (licenseNode.Attributes["type"].Value)
					{
						// SPDX expression as defined in https://spdx.org/spdx-specification-21-web-version#h.jxpfx0ykyb60
						case "expression":
							license = licenseNode.InnerText;
							break;
						// license is contained as file within the nuget package
						case "file":
							string licensePath = Path.Combine(Path.GetDirectoryName(nuSpecFilePath),
								licenseNode.InnerText);
							if (!File.Exists(licensePath))
							{
								sLog.Write(LogLevel.Error,
									"The path '{0}' to the license defined by the NuGet specification does not exists.",
									licensePath);
								break;
							}
							license = File.ReadAllText(licensePath);
							break;
					}
				}

				// download license when only url to license is given
				if (license == null && licenseUrl != null && licenseUrl.Contains("github.com"))
				{
					string url = licenseUrl.Replace("/blob/", "/raw/");
					try
					{
						using (var client = new WebClient())
						{
							license = client.DownloadString(url);
							sLog.Write(LogLevel.Developer, "Successful downloaded license '{0}' for {1}", url, identifier);
						}
					}
					catch (WebException ex)
					{
						sLog.Write(LogLevel.Error, "Error during downloading '{0}': {1}", url, ex.Message);
					}
				}

				if (license == null && licenseUrl == null)
				{
					sLog.Write(LogLevel.Error, "The NuGet specification file '{0}' does not contain valid license information", nuSpecFilePath);
					continue;
				}

				var package = new PackageLicenseInfo(identifier, version, authors, copyright, licenseUrl, projectUrl, license);
				mLicenses.Add(package);
				sLog.Write(LogLevel.Developer, "Successful extract license information for '{0} v{1}'.", identifier, version);
			}
			sLog.Write(LogLevel.Note, "Successful extract license information from found NuGet packages.");
			sLog.Write(LogLevel.Note, "--------------------------------------------------------------------------------");
		}

		#endregion

		#region Get static license information project folder
		/// <summary>
		/// Inspect given project folders for static licenses and load them.
		/// </summary>
		public void GetStaticLicenseInfo()
		{
			if (mFinishProcessing)
				return;

			foreach (ProjectInfo project in mProjectsToProcess)
			{
				string projectDir = Path.GetDirectoryName(project.ProjectAbsolutePath);

				IEnumerable<string> staticProjectLicenses = Directory.EnumerateFiles(projectDir, mSearchPattern, SearchOption.AllDirectories);

				if (staticProjectLicenses != null && staticProjectLicenses.Any())
				{
					foreach (string staticLicensePath in staticProjectLicenses)
					{
						sLog.Write(LogLevel.Developer, "Project '{0}': Found static license '{1}'", project.ProjectName, staticLicensePath);
						string staticLicenseIdentifier = Path.GetFileNameWithoutExtension(staticLicensePath);
						string license = File.ReadAllText(staticLicensePath);
						// static license is already existing in another project
						if (mLicenses.Any(x => x.PackageIdentifier == staticLicenseIdentifier))
						{
							sLog.Write(LogLevel.Developer, "Static license '{0}' was already found in another project.", staticLicenseIdentifier);
							continue;
						}
						mLicenses.Add(new PackageLicenseInfo(staticLicenseIdentifier, license));
					}
				}
				else
				{
					sLog.Write(LogLevel.Developer, "No static licenses found under project directory '{0}'", projectDir);
				}
			}
			sLog.Write(LogLevel.Note, "Successful scan project directories for static licenses.");
			sLog.Write(LogLevel.Note, "--------------------------------------------------------------------------------");
		}

		#endregion

		#region Generate output file
		/// <summary>
		/// Generate output third party notice with collected license information.
		/// </summary>
		public void GenerateOutputFile()
		{
			if (mLicenses == null || mLicenses.Count == 0 || mFinishProcessing)
			{
				sLog.Write(LogLevel.Note, "Nothing to store, because there are no licenses found.");
				return;
			}

			// overwrite existing file if necessary
			if (File.Exists(mOutputPath))
				File.Delete(mOutputPath);

			foreach (PackageLicenseInfo license in mLicenses)
			{
				var builder = new StringBuilder();
				builder.AppendLine("--------------------------------------------------------------------------------");
				builder.AppendLine(license.ToString());
				builder.AppendLine("--------------------------------------------------------------------------------");
				File.AppendAllText(mOutputPath, builder.ToString());
				sLog.Write(LogLevel.Developer, "Append license information for '{0}'", license.PackageIdentifier);
			}

			sLog.Write(LogLevel.Note, "Successful write collected licenses to '{0}'", mOutputPath);
			sLog.Write(LogLevel.Note, "--------------------------------------------------------------------------------");
		}
		#endregion
	}
}
