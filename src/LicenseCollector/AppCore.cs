using GriffinPlus.Lib.Logging;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
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
				// only c# projects get processed which are build with given "configuration|platform"
				if (project.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat &&
				    Path.GetExtension(project.AbsolutePath).Equals(".csproj") && project.ProjectConfigurations.Keys.Any(
					    x => x.Equals($"{mConfiguration}|{mPlatform}") && project.ProjectConfigurations[x].IncludeInBuild))
				{
					string[] packagesConfig = Directory.EnumerateFiles(Path.GetDirectoryName(project.AbsolutePath), cPackagesConfig,
						SearchOption.AllDirectories).ToArray();
					// project uses packages.config
					if (packagesConfig.Length == 1)
					{
						sLog.Write(LogLevel.Developer, "Found '{0}' for '{1}'.", packagesConfig[0], project.ProjectName);
						mProjectsToProcess.Add(new ProjectInfo(project.ProjectName, project.AbsolutePath,
							packagesConfig[0], NuGetStyle.PackagesConfig));
						continue;
					}
					//  ignore project when more than one 'packages.config' exists
					if (packagesConfig.Length > 1)
					{
						sLog.Write(LogLevel.Error, "There are more than one '{0}' found for '{1}'. Skip project.", cPackagesConfig, project.ProjectName);
						mProjectsToProcess.Add(new ProjectInfo(project.ProjectName, project.AbsolutePath, "",
							NuGetStyle.Undefined));
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
							NuGetStyle.Undefined));
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
					if (!File.Exists(projectAssetsPath))
					{
						sLog.Write(LogLevel.Error, "No '{0}' found for '{1}'. Skip project.", cProjectAssets,
							project.ProjectName);
						mProjectsToProcess.Add(new ProjectInfo(project.ProjectName, project.AbsolutePath, "",
							NuGetStyle.Undefined));
						continue;
					}

					mProjectsToProcess.Add(new ProjectInfo(project.ProjectName, project.AbsolutePath, projectAssetsPath,
						NuGetStyle.PackageReference));
					sLog.Write(LogLevel.Developer, "Found '{0}' for '{1}'.", projectAssetsPath, project.ProjectName);
				}
				// process c++ projects
				else if (project.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat &&
				         Path.GetExtension(project.AbsolutePath).Equals(".vcxproj")) // TODO: condition to include only build c++ projects
				{
					string[] packagesConfig = Directory.EnumerateFiles(Path.GetDirectoryName(project.AbsolutePath), cPackagesConfig,
						SearchOption.AllDirectories).ToArray();
					// project uses packages.config
					if (packagesConfig.Length != 1)
					{
						sLog.Write(LogLevel.Error, "There are more than one or no '{0}' found for '{1}'. Skip project.", cPackagesConfig, project.ProjectName);
						mProjectsToProcess.Add(new ProjectInfo(project.ProjectName, project.AbsolutePath, "",
							NuGetStyle.Undefined));
						continue;
					}
					sLog.Write(LogLevel.Developer, "Found '{0}' for '{1}'.", packagesConfig[0], project.ProjectName);
					mProjectsToProcess.Add(new ProjectInfo(project.ProjectName, project.AbsolutePath,
						packagesConfig[0], NuGetStyle.PackagesConfig));
				}
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

			foreach (ProjectInfo project in mProjectsToProcess)
			{
				sLog.Write(LogLevel.Developer, "Determine NuGet packages for '{0}' from '{1}'.",
					project.ProjectName,
					project.NuGetInformationPath);
				switch (project.Type)
				{
					case NuGetStyle.PackageReference:
					{
						GetNuGetPackageFromProjectAssets(project);
						break;
					}
					case NuGetStyle.PackagesConfig:
					{
						GetNuGetPackagesFromPackagesConfig(project);
						break;
					}
					case NuGetStyle.Undefined:
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
		private void GetNuGetPackagesFromPackagesConfig(ProjectInfo project)
		{
			throw new NotImplementedException();
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
				var identifier = string.Empty;
				var version = string.Empty;
				var authors = string.Empty;
				var licenseUrl = string.Empty;
				var projectUrl = string.Empty;
				var license = string.Empty;

				// extract important information from NuGet specification file
				foreach (XElement element in XElement.Load(nuSpecFilePath).DescendantsAndSelf())
				{
					switch (element.Name.LocalName)
					{
						case "id":
							identifier = element.Value;
							break;
						case "version":
							version = element.Value;
							break;
						case "authors":
							authors = element.Value;
							break;
						// licenseUrl is deprecated and now 'license' should be used.
						case "licenseUrl":
							if (element.Value != cDeprecatedLicenseUrl)
								licenseUrl = element.Value;
							break;
						case "projectUrl":
							projectUrl = element.Value;
							break;
						case "license":
							switch (element.Attribute("type").Value)
							{
								// SPDX expression as defined in https://spdx.org/spdx-specification-21-web-version#h.jxpfx0ykyb60
								case "expression":
									license = element.Value;
									break;
								// license is contained as file within the nuget package
								case "file":
									string licensePath = Path.Combine(Path.GetDirectoryName(nuSpecFilePath),
										element.Value);
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
							break;
					}
				}

				// download license when only url to license is given
				if (string.IsNullOrEmpty(license) && licenseUrl != string.Empty && licenseUrl.Contains("github.com"))
				{
					string url = licenseUrl.Replace("/blob/", "/raw/");
					using (var client = new WebClient())
					{
						license = client.DownloadString(url);
						sLog.Write(LogLevel.Developer, "Successful downloaded license '{0}' for {1}", url, identifier);
					}
				}

				if (string.IsNullOrEmpty(license) && string.IsNullOrEmpty(licenseUrl))
				{
					sLog.Write(LogLevel.Error, "The NuGet specification file '{0}' does not contain valid license information", nuSpecFilePath);
					continue;
				}

				var package = new PackageLicenseInfo(identifier, version, authors, licenseUrl, projectUrl, license);
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
