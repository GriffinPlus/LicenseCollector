using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GriffinPlus.LicenseCollector;

/// <summary>
/// Evaluates properties of MSBuild projects by running 'dotnet msbuild -getProperty' in a separate process.
/// This keeps the application independent of the MSBuild version shipped with the installed .NET SDK.
/// </summary>
public static class MsBuildPropertyEvaluator
{
	/// <summary>
	/// Maximum time to wait for a single evaluation to complete.
	/// </summary>
	private static readonly TimeSpan sEvaluationTimeout = TimeSpan.FromMinutes(5);

	/// <summary>
	/// Environment variables that are removed from the environment of the child process,
	/// because they might have been inherited from a calling MSBuild instance and would pin
	/// the child process to an SDK that is not selected by 'global.json'.
	/// </summary>
	private static readonly string[] sEnvironmentVariablesToRemove =
	[
		"MSBuildExtensionsPath",
		"MSBuildSDKsPath",
		"MSBUILD_EXE_PATH"
	];

	/// <summary>
	/// Evaluates the specified properties of an MSBuild project.
	/// </summary>
	/// <param name="projectPath">Absolute path of the project file to evaluate.</param>
	/// <param name="globalProperties">Global properties to pass to MSBuild (e.g. 'Configuration' and 'Platform').</param>
	/// <param name="propertyNames">Names of the properties to evaluate.</param>
	/// <param name="workingDirectory">
	/// Working directory of the 'dotnet' process. It determines which 'global.json' is used to select the .NET SDK.
	/// </param>
	/// <param name="cancellationToken">Token that can be signaled to cancel the operation.</param>
	/// <returns>The evaluated property values (key: property name, value: evaluated value, empty if not defined).</returns>
	/// <exception cref="InvalidOperationException">Starting 'dotnet' or evaluating the project failed.</exception>
	public static async Task<IReadOnlyDictionary<string, string>> EvaluateAsync(
		string                              projectPath,
		IReadOnlyDictionary<string, string> globalProperties,
		IReadOnlyCollection<string>         propertyNames,
		string                              workingDirectory,
		CancellationToken                   cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(projectPath);
		ArgumentNullException.ThrowIfNull(globalProperties);
		ArgumentNullException.ThrowIfNull(propertyNames);
		if (propertyNames.Count == 0) throw new ArgumentException("At least one property name must be specified.", nameof(propertyNames));

		string dotnetPath = GetDotnetHostPath();
		var startInfo = new ProcessStartInfo(dotnetPath)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = workingDirectory
		};

		startInfo.ArgumentList.Add("msbuild");
		startInfo.ArgumentList.Add(projectPath);
		startInfo.ArgumentList.Add("-nologo");
		startInfo.ArgumentList.Add("-tl:off");
		foreach (KeyValuePair<string, string> property in globalProperties)
		{
			startInfo.ArgumentList.Add($"-p:{property.Key}={EscapePropertyValue(property.Value)}");
		}

		foreach (string propertyName in propertyNames)
		{
			startInfo.ArgumentList.Add($"-getProperty:{propertyName}");
		}

		// suppress output that does not belong to the evaluation result (first run experience, telemetry notice etc.)
		startInfo.Environment["DOTNET_NOLOGO"] = "1";
		startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
		startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
		startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
		startInfo.Environment["MSBUILDTERMINALLOGGER"] = "off";
		foreach (string name in sEnvironmentVariablesToRemove)
		{
			startInfo.Environment.Remove(name);
		}

		using var process = new Process();
		process.StartInfo = startInfo;

		try
		{
			process.Start();
		}
		catch (Win32Exception ex)
		{
			throw new InvalidOperationException(
				$"Starting '{dotnetPath}' failed. Please ensure that the .NET SDK (8.0.100 or later) is installed and 'dotnet' is in the PATH.",
				ex);
		}

		process.StandardInput.Close();

		// read both streams concurrently to avoid deadlocks when a pipe buffer runs full
		Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
		Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

		using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
		{
			timeoutCts.CancelAfter(sEvaluationTimeout);
			try
			{
				await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				try { process.Kill(entireProcessTree: true); }
				catch (InvalidOperationException)
				{
					/* process has already exited */
				}

				cancellationToken.ThrowIfCancellationRequested();
				throw new InvalidOperationException($"Evaluating project '{projectPath}' did not complete within {sEvaluationTimeout.TotalMinutes} minutes.");
			}
		}

		string stdout = await stdoutTask.ConfigureAwait(false);
		string stderr = await stderrTask.ConfigureAwait(false);

		if (process.ExitCode != 0)
		{
			throw new InvalidOperationException(
				$"Evaluating project '{projectPath}' failed (exit code: {process.ExitCode}). " +
				"Evaluating properties requires the .NET SDK 8.0.100 or later.\n" +
				$"Command: \"{dotnetPath}\" {string.Join(" ", startInfo.ArgumentList.Select(QuoteArgument))}\n" +
				$"Output:\n{string.Join("\n", new[] { stdout.Trim(), stderr.Trim() }.Where(x => x.Length > 0))}");
		}

		return ParseOutput(stdout, propertyNames, projectPath);
	}

	/// <summary>
	/// Parses the output of 'dotnet msbuild -getProperty'.
	/// MSBuild prints the plain value if a single property is requested and a JSON object otherwise.
	/// </summary>
	/// <param name="output">Standard output of the MSBuild process.</param>
	/// <param name="propertyNames">Names of the requested properties.</param>
	/// <param name="projectPath">Path of the evaluated project (for error messages only).</param>
	/// <returns>The evaluated property values.</returns>
	internal static IReadOnlyDictionary<string, string> ParseOutput(
		string                      output,
		IReadOnlyCollection<string> propertyNames,
		string                      projectPath)
	{
		var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		if (propertyNames.Count == 1)
		{
			// the value is the last line of the output (any preceding lines are unexpected noise)
			string value = output
				               .Split('\n')
				               .Select(x => x.TrimEnd('\r'))
				               .LastOrDefault(x => x.Trim().Length > 0) ?? "";
			result[propertyNames.First()] = value.Trim();
			return result;
		}

		int start = output.IndexOf('{');
		int end = output.LastIndexOf('}');
		if (start < 0 || end < start)
		{
			throw new InvalidOperationException($"Evaluating project '{projectPath}' returned unexpected output:\n{output}");
		}

		JObject json;
		try
		{
			json = JObject.Parse(output.Substring(start, end - start + 1));
		}
		catch (JsonReaderException ex)
		{
			throw new InvalidOperationException(
				$"Evaluating project '{projectPath}' returned malformed output:\n{output}",
				ex);
		}

		if (json["Properties"] is not JObject properties)
		{
			throw new InvalidOperationException($"Evaluating project '{projectPath}' returned output without properties:\n{output}");
		}

		foreach (string propertyName in propertyNames)
		{
			result[propertyName] = properties[propertyName]?.Value<string>() ?? "";
		}

		return result;
	}

	/// <summary>
	/// Gets the path of the 'dotnet' host to use.
	/// </summary>
	/// <returns>Path of the 'dotnet' executable.</returns>
	private static string GetDotnetHostPath()
	{
		// set by the .NET CLI when running inside 'dotnet build', 'dotnet run' etc.
		string hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
		if (!string.IsNullOrEmpty(hostPath) && File.Exists(hostPath))
			return hostPath;

		// the application is run via 'dotnet LicenseCollector.dll'
		string processPath = Environment.ProcessPath;
		if (!string.IsNullOrEmpty(processPath) &&
		    Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
		{
			return processPath;
		}

		// look up 'dotnet' in the PATH
		return "dotnet";
	}

	/// <summary>
	/// Escapes characters with special meaning in property values passed via the MSBuild command line.
	/// </summary>
	/// <param name="value">Value to escape.</param>
	/// <returns>The escaped value.</returns>
	private static string EscapePropertyValue(string value)
	{
		return value
			.Replace("%", "%25")
			.Replace(";", "%3B")
			.Replace(",", "%2C");
	}

	/// <summary>
	/// Quotes a command line argument for display purposes.
	/// </summary>
	/// <param name="argument">Argument to quote.</param>
	/// <returns>The quoted argument.</returns>
	private static string QuoteArgument(string argument)
	{
		return argument.Contains(' ') ? $"\"{argument}\"" : argument;
	}
}
