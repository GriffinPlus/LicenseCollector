using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace GriffinPlus.LicenseCollector;

/// <summary>
/// Tests running the built LicenseCollector executable against a temporary solution.
/// </summary>
public sealed class ApplicationTests : IDisposable
{
	private const int SuccessExitCode = 0;

	/// <summary>
	/// Exit code of the application indicating a general error (see Program.ExitCode.GeneralError).
	/// </summary>
	private const int GeneralErrorExitCode = 2;

	private static readonly TimeSpan sProcessTimeout = TimeSpan.FromMinutes(2);

	private const string SolutionFileName = "Test.slnx";
	private const string TemplateFileName = "Test.template";
	private const string ProjectName      = "Test Project";

	private const string ValidProjectContent =
		"""
		<Project Sdk="Microsoft.NET.Sdk">
			<PropertyGroup>
				<TargetFramework>net11.0</TargetFramework>
			</PropertyGroup>
		</Project>
		""";

	private readonly string mWorkingDirectory;

	/// <summary>
	/// Initializes a new instance of the <see cref="ApplicationTests"/> class
	/// creating a temporary directory with a solution containing a single project.
	/// </summary>
	public ApplicationTests()
	{
		mWorkingDirectory = Path.Combine(Path.GetTempPath(), $"LicenseCollector Tests {Guid.NewGuid():N}");
		Directory.CreateDirectory(Path.Combine(mWorkingDirectory, ProjectName));
		File.WriteAllText(
			Path.Combine(mWorkingDirectory, SolutionFileName),
			$"""
			 <Solution>
			 	<Project Path="{ProjectName}/{ProjectName}.csproj" />
			 </Solution>
			 """);
		File.WriteAllText(
			Path.Combine(mWorkingDirectory, TemplateFileName),
			"@foreach (var license in Model.Licenses) { @license.PackageIdentifier }");
	}

	/// <summary>
	/// Removes the temporary directory.
	/// </summary>
	public void Dispose()
	{
		try
		{
			Directory.Delete(mWorkingDirectory, true);
		}
		catch (IOException)
		{
			// best effort only, the directory is located in the temporary directory
		}
	}

	/// <summary>
	/// Checks that specifying a configuration that does not exist in the solution fails.
	/// </summary>
	[Fact]
	public async Task ConfigurationDoesNotExist()
	{
		WriteProjectFile(ValidProjectContent);

		(int exitCode, string output) = await RunLicenseCollectorAsync("DoesNotExist", "Any CPU");

		Assert.True(exitCode == GeneralErrorExitCode, $"Unexpected exit code {exitCode}. Output:\n{output}");
		Assert.Contains("'DoesNotExist|Any CPU' does not match an existing solution configuration", output);
	}

	/// <summary>
	/// Checks that a missing 'dotnet' executable is reported when the project has to be evaluated.
	/// </summary>
	[Fact]
	public async Task NoDotnetInPath()
	{
		WriteProjectFile(ValidProjectContent);

		(int exitCode, string output) = await RunLicenseCollectorAsync(
			                                "Release",
			                                "Any CPU",
			                                environment =>
			                                {
				                                // the application must neither find 'dotnet' via the PATH nor via the variable set by the .NET CLI
				                                environment.Remove("DOTNET_HOST_PATH");
				                                environment["PATH"] = string.Join(
					                                Path.PathSeparator,
					                                (environment.TryGetValue("PATH", out string path) ? path ?? "" : "")
					                                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
					                                .Where(x => !ContainsDotnetExecutable(x)));

				                                // the apphost must still be able to find the runtime
				                                environment["DOTNET_ROOT"] = GetDotnetRootDirectory();
			                                });

		Assert.True(exitCode == GeneralErrorExitCode, $"Unexpected exit code {exitCode}. Output:\n{output}");
		Assert.Contains("Starting 'dotnet' failed", output);
	}

	/// <summary>
	/// Checks that a project file that cannot be loaded by MSBuild fails and passes the MSBuild error through.
	/// </summary>
	[Fact]
	public async Task DefectProjectFile()
	{
		WriteProjectFile(ValidProjectContent + "<Broken>");

		(int exitCode, string output) = await RunLicenseCollectorAsync("Release", "Any CPU");

		Assert.True(exitCode == GeneralErrorExitCode, $"Unexpected exit code {exitCode}. Output:\n{output}");
		Assert.Contains("MSB4025", output);
	}

	/// <summary>
	/// Checks that test projects are skipped as their dependencies are not shipped.
	/// </summary>
	[Fact]
	public async Task TestProjectIsSkipped()
	{
		WriteProjectFile(
			"""
			<Project Sdk="Microsoft.NET.Sdk">
				<PropertyGroup>
					<TargetFramework>net11.0</TargetFramework>
					<IsTestProject>true</IsTestProject>
				</PropertyGroup>
			</Project>
			""");

		(int exitCode, string output) = await RunLicenseCollectorAsync("Release", "Any CPU");

		Assert.True(exitCode == SuccessExitCode, $"Unexpected exit code {exitCode}. Output:\n{output}");
		Assert.Contains($"Skipping test project '{ProjectName}'", output);
		Assert.DoesNotContain("project.assets.json", output);
	}

	#region Helpers

	private void WriteProjectFile(string content)
	{
		File.WriteAllText(Path.Combine(mWorkingDirectory, ProjectName, $"{ProjectName}.csproj"), content);
	}

	/// <summary>
	/// Runs the native executable (apphost) of the LicenseCollector, so the application does not run
	/// in a 'dotnet' process and has to look up 'dotnet' on its own.
	/// </summary>
	private async Task<(int ExitCode, string Output)> RunLicenseCollectorAsync(
		string                              configuration,
		string                              platform,
		Action<IDictionary<string, string>> configureEnvironment = null)
	{
		string executablePath = Path.Combine(
			AppContext.BaseDirectory,
			OperatingSystem.IsWindows() ? "LicenseCollector.exe" : "LicenseCollector");
		Assert.True(File.Exists(executablePath), $"The LicenseCollector executable was not found at '{executablePath}'.");

		var startInfo = new ProcessStartInfo(executablePath)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = mWorkingDirectory
		};

		startInfo.ArgumentList.Add("--solutionFilePath");
		startInfo.ArgumentList.Add(Path.Combine(mWorkingDirectory, SolutionFileName));
		startInfo.ArgumentList.Add("--configuration");
		startInfo.ArgumentList.Add(configuration);
		startInfo.ArgumentList.Add("--platform");
		startInfo.ArgumentList.Add(platform);
		startInfo.ArgumentList.Add("--licenseTemplatePath");
		startInfo.ArgumentList.Add(Path.Combine(mWorkingDirectory, TemplateFileName));
		startInfo.ArgumentList.Add(Path.Combine(mWorkingDirectory, "THIRD_PARTY_NOTICES"));

		configureEnvironment?.Invoke(startInfo.Environment);

		using Process process = Process.Start(startInfo);
		Assert.NotNull(process);
		Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
		Task<string> stderrTask = process.StandardError.ReadToEndAsync();

		using (var cts = new CancellationTokenSource(sProcessTimeout))
		{
			try
			{
				await process.WaitForExitAsync(cts.Token);
			}
			catch (OperationCanceledException)
			{
				process.Kill(entireProcessTree: true);
				Assert.Fail($"The LicenseCollector did not exit within {sProcessTimeout.TotalMinutes} minutes.");
			}
		}

		string output = await stdoutTask + await stderrTask;
		return (process.ExitCode, output);
	}

	private static bool ContainsDotnetExecutable(string directory)
	{
		try
		{
			return File.Exists(Path.Combine(directory.Trim('"'), OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
		}
		catch (ArgumentException)
		{
			return false;
		}
	}

	/// <summary>
	/// Gets the root directory of the .NET installation running the tests
	/// (the runtime directory is '[root]/shared/Microsoft.NETCore.App/[version]/').
	/// </summary>
	private static string GetDotnetRootDirectory()
	{
		return Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
	}

	#endregion
}
