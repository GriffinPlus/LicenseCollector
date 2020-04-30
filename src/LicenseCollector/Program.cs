using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using CommandLine;
using GriffinPlus.Lib.Logging;

namespace GriffinPlus.LicenseCollector
{
	/// <summary>
	/// Main entry and starting point of application.
	/// </summary>
	class Program
	{
		private static readonly LogWriter sLog = Log.GetWriter<Program>();

		/// <summary>
		/// Command line argument mapping class
		/// (see https://github.com/commandlineparser/commandline for details).
		/// </summary>
		class Options
		{
			/// <summary>
			/// Gets and sets verbosity of this tool.
			/// </summary>
			[Option('v', Default = false)]
			public bool Verbose { get; set; }

			/// <summary>
			/// Gets and sets path to solution file to examine licenses for.
			/// </summary>
			[Option('s', "solutionFilePath", Required = true)]
			public string SolutionFilePath { get; set; }

			/// <summary>
			/// Gets and sets used configuration of the solution file.
			/// </summary>
			[Option('c', "configuration", Required = true)]
			public string Configuration { get; set; }

			/// <summary>
			/// Gets and sets used platform of the solution file.
			/// </summary>
			[Option('p', "platform", Required = true)]
			public string Platform { get; set; }

			/// <summary>
			/// Gets and sets search pattern for static license files. Supports pattern used by <see cref="Directory.EnumerateFiles(string)"/>.
			/// </summary>
			[Option("searchPattern", Default = "*.License")]
			public string SearchPattern { get; set; }

			/// <summary>
			/// Gets and sets path to license template.
			/// </summary>
			[Option("licenseTemplatePath", Default = "")]
			public string LicenseTemplatePath { get; set; }

			/// <summary>
			/// Gets and sets path to the output license file.
			/// </summary>
			[Value(0, Required = true)]
			public string OutputLicensePath { get; set; }
		}

		/// <summary>
		/// Exit codes returned by the application.
		/// </summary>
		internal enum ExitCode
		{
			Success = 0,
			ArgumentError = 1,
			GeneralError = 2,
			FileNotFound = 3
		}

		static int Main(string[] args)
		{
			// configure the log
			var formatter = new TableMessageFormatter();
			formatter.AddTimestampColumn("yyyy-MM-dd HH:mm:ss.fff");
			formatter.AddLogLevelColumn();
			formatter.AddTextColumn();
			var consoleStage = new ConsoleWriterPipelineStage();
			consoleStage.Formatter = formatter;
			Log.LogMessageProcessingPipeline = consoleStage;

			// configure command line parser
			var parser = new Parser(with =>
			{
				with.CaseInsensitiveEnumValues = true;
				with.CaseSensitive = false;
				with.EnableDashDash = true;
				with.IgnoreUnknownArguments = false;
				with.ParsingCulture = CultureInfo.InvariantCulture;
				with.HelpWriter = null;
			});

			//process command line
			int exitCode = parser.ParseArguments<Options>(args)
				.MapResult(
					options => (int) RunOptionsAndReturnExitCode(options),
					errors => (int) HandleParseError(errors));

			return exitCode;
		}

		#region Command Line Processing
		/// <summary>
		/// Is called, if specified command line arguments have successfully been validated.
		/// </summary>
		/// <param name="options">Command line options.</param>
		/// <returns>Exit code the application should return.</returns>
		private static ExitCode RunOptionsAndReturnExitCode(Options options)
		{
			// configure the log verbosity
			var configuration = new VolatileLogConfiguration();
			configuration.AddLogWriterDefault(x => x.WithBaseLevel(options.Verbose ? LogLevel.All: LogLevel.Note));
			Log.Configuration = configuration;

			sLog.Write(LogLevel.Developer, "LicenseCollector v{0}", Assembly.GetExecutingAssembly().GetName().Version);
			sLog.Write(LogLevel.Developer, "--------------------------------------------------------------------------------");
			sLog.Write(LogLevel.Developer, "Verbose:            '{0}'", options.Verbose);
			sLog.Write(LogLevel.Developer, "SolutionFile:       '{0}'", options.SolutionFilePath);
			sLog.Write(LogLevel.Developer, "Configuration:      '{0}'", options.Configuration);
			sLog.Write(LogLevel.Developer, "Platform:           '{0}'", options.Platform);
			sLog.Write(LogLevel.Developer, "SearchPattern:      '{0}'", options.SearchPattern);
			sLog.Write(LogLevel.Developer, "LicenseTemplatePath '{0}'", options.LicenseTemplatePath);
			sLog.Write(LogLevel.Developer, "OutputPath:         '{0}'", options.OutputLicensePath);
			sLog.Write(LogLevel.Developer, "--------------------------------------------------------------------------------");

			// the given path for the solution does not exist
			if (!File.Exists(options.SolutionFilePath))
			{
				sLog.Write(LogLevel.Error, "The path to the solution file under '{0}' does not exist.", options.SolutionFilePath);
				return ExitCode.FileNotFound;
			}
			// the given path is not a solution file
			if (!Path.GetExtension(options.SolutionFilePath).Equals(".sln"))
			{
				sLog.Write(LogLevel.Error, "The path '{0}' is not a solution file.", options.SolutionFilePath);
				return ExitCode.ArgumentError;
			}
			// convert given relative paths to absolute paths if necessary
			if (!Path.IsPathRooted(options.SolutionFilePath))
				options.SolutionFilePath = Path.GetFullPath(options.SolutionFilePath);
			if (!Path.IsPathRooted(options.OutputLicensePath))
				options.OutputLicensePath = Path.GetFullPath(options.OutputLicensePath);
			if (!string.IsNullOrEmpty(options.LicenseTemplatePath) && !Path.IsPathRooted(options.LicenseTemplatePath))
				options.LicenseTemplatePath = Path.GetFullPath(options.LicenseTemplatePath);

			var app = new AppCore(options.SolutionFilePath, options.Configuration, options.Platform,
				options.OutputLicensePath, options.SearchPattern, options.LicenseTemplatePath);
			try
			{
				app.CollectProjects();

				app.GetNuGetPackages();

				app.GetNuGetLicenseInfo();

				app.GetStaticLicenseInfo();

				app.GenerateOutputFileAsync().Wait();
			}
			catch (Exception ex)
			{
				sLog.Write(LogLevel.Error, "Caught exception during processing. Exception: {0}", ex);
				return ExitCode.GeneralError;
			}

			return ExitCode.Success;
		}

		private static ExitCode HandleParseError(IEnumerable<Error> errors)
		{
			if (errors.Any(x => x.Tag == ErrorType.HelpRequestedError))
			{
				PrintUsage(null, Console.Out);
				return ExitCode.Success;
			}
			if (errors.Any(x => x.Tag == ErrorType.VersionRequestedError))
			{
				PrintVersion(Console.Out);
				return ExitCode.Success;
			}

			PrintUsage(errors, Console.Error);
			return ExitCode.ArgumentError;
		}

		#endregion

		#region Usage Information / Error Reporting
		/// <summary>
		/// Writes usage text (with an optional error section).
		/// </summary>
		/// <param name="errors">Command line parsing errors (null, if no error occurred).</param>
		/// <param name="writer">Text writer to use.</param>
		private static void PrintUsage(IEnumerable<Error> errors, TextWriter writer)
		{
			writer.WriteLine($"  LicenseCollector v{Assembly.GetExecutingAssembly().GetName().Version}");
			writer.WriteLine("--------------------------------------------------------------------------------");

			if (errors != null && errors.Any())
			{
				writer.WriteLine();
				writer.WriteLine("  ERRORS:");
				writer.WriteLine();

				foreach (Error error in errors)
				{
					switch (error.Tag)
					{
						case ErrorType.UnknownOptionError:
						{
							var err = (UnknownOptionError) error;
							writer.WriteLine($"    - Unknown option: {err.Token}");
							break;
						}
						case ErrorType.RepeatedOptionError:
						{
							var err = (RepeatedOptionError) error;
							writer.WriteLine($"    - Repeated option: -{err.NameInfo.ShortName}, --{err.NameInfo.LongName}");
							break;
						}
						case ErrorType.MissingRequiredOptionError:
						{
							var err = (MissingRequiredOptionError) error;
							if (string.IsNullOrEmpty(err.NameInfo.LongName) && string.IsNullOrEmpty(err.NameInfo.ShortName))
							{
								writer.WriteLine($"    - Missing required value: <outpath>");
							}
							else
							{
								writer.WriteLine($"    - Missing required option: -{err.NameInfo.ShortName}, --{err.NameInfo.LongName}");
							}
							break;
						}
						default:
						{
							writer.WriteLine("    - Unspecified command line error");
							break;
						}
					}
				}

				writer.WriteLine();
				writer.WriteLine("--------------------------------------------------------------------------------");
			}

			writer.WriteLine();
			writer.WriteLine("  USAGE:");
			writer.WriteLine();
			writer.WriteLine(
				"    LicenseCollector.exe [-v] -s|--solutionFilePath <spath> -c|--configuration <conf> -p|--platform <platform> [--searchPattern <pattern>] [--licenseTemplatePath <templatePath>] <outpath>");
			writer.WriteLine();
			writer.WriteLine("    [-v]");
			writer.WriteLine("      Sets output to verbose.");
			writer.WriteLine();
			writer.WriteLine("    -s|--solutionFilePath <spath>");
			writer.WriteLine("      The path to the Visual Studio solution file for which to collect licenses.");
			writer.WriteLine();
			writer.WriteLine("    -c|--configuration <conf>");
			writer.WriteLine("      The build configuration of the solution, e.g. 'Release' or 'Debug'.");
			writer.WriteLine();
			writer.WriteLine("    -p|--platform <platform>");
			writer.WriteLine("      The build platform of the solution, e.g. 'x64' or 'x86'.");
			writer.WriteLine();
			writer.WriteLine("    [--searchPattern <pattern>]");
			writer.WriteLine("      The search pattern for static license files. Wildcards like '*' are supported.");
			writer.WriteLine();
			writer.WriteLine("    [--licenseTemplatePath <templatePath>]");
			writer.WriteLine("      The path to a razor template file used when generating output file.");
			writer.WriteLine();
			writer.WriteLine("    <outpath>");
			writer.WriteLine("      The path of the file where the results should be written to.");
			writer.WriteLine();
			writer.WriteLine("--------------------------------------------------------------------------------");
		}

		/// <summary>
		/// Writes version information.
		/// </summary>
		/// <param name="writer">Text writer to use.</param>
		private static void PrintVersion(TextWriter writer)
		{
			writer.WriteLine($"  LicenseCollector v{Assembly.GetExecutingAssembly().GetName().Version}");
		}
		#endregion
	}
}
