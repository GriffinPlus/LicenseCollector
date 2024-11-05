using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using GriffinPlus.Lib.Logging;

namespace GriffinPlus.LicenseCollector.Razor;

/// <summary>
/// Representing a template compiled by the <see cref="IRazorEngineCompiler"/>.
/// </summary>
public class RazorEngineCompiledTemplate
{
	private static readonly LogWriter sLog = LogWriter.Get<RazorEngineCompiledTemplate>();

	private string TemplateFileName { get; }

	/// <summary>
	/// Representing the type of the compiled template.
	/// </summary>
	protected Type TemplateType { get; set; }

	internal RazorEngineCompiledTemplate(
		string templateNameSpace,
		byte[] assemblyByteCode,
		byte[] pdbByteCode,
		string templateFileName = "",
		string templateSource   = "")
	{
		if (string.IsNullOrEmpty(templateNameSpace)) throw new ArgumentException($"The {nameof(templateNameSpace)} must not be empty.");
		if (assemblyByteCode == null || assemblyByteCode.Length == 0) throw new ArgumentException($"An {nameof(assemblyByteCode)} must be provided.");

		sLog.Write(LogLevel.Debug, "Loading dynamic assembly of template {0}...", templateFileName);
		var assembly = Assembly.Load(assemblyByteCode, pdbByteCode);
		TemplateType = assembly.GetType(templateNameSpace + ".Template");
		if (TemplateType == null) throw new InvalidOperationException($"The assembly does not contain a type named {templateNameSpace}.Template'.");

		sLog.Write(LogLevel.Debug, "Loading dynamic assembly finished and found template type {0}.", TemplateType.FullName);

		TemplateFileName = templateFileName;

		if (pdbByteCode is { Length: > 0 } && !string.IsNullOrWhiteSpace(templateSource))
			EnableDebugging(templateSource);
	}

	/// <summary>
	/// Fills the template with the given data and returns a string.
	/// </summary>
	/// <param name="isHtml">Whether the template to fill is an HTML template.</param>
	/// <param name="model">The model to be used to fill the template gaps.</param>
	/// <returns>A string representing the content of the requested template, filled with all gaps.</returns>
	public string Run(bool isHtml, object model = null)
	{
		return RunAsync(isHtml, model).GetAwaiter().GetResult();
	}

	/// <summary>
	/// Fills the template with the given data and returns a string.
	/// </summary>
	/// <param name="isHtml">Whether the template to fill is an HTML template.</param>
	/// <param name="model">The model to be used to fill the template gaps.</param>
	/// <param name="cancellationToken">Cancellation token that can be signaled to abort the operation.</param>
	/// <returns>A string representing the content of the requested template, filled with all gaps.</returns>
	public async Task<string> RunAsync(
		bool              isHtml,
		object            model             = null,
		CancellationToken cancellationToken = default)
	{
		model = model != null ? new AnonymousTypeWrapper(model) : null;

		sLog.Write(LogLevel.Debug, "Creating instance of {0}...", TemplateType.FullName);
		var instance = (IRazorEngineTemplate)Activator.CreateInstance(TemplateType) ?? throw new InvalidOperationException($"Could not create an instance of {TemplateType.FullName}.");
		instance.Model = model;
		instance.IsHtml = isHtml;

		sLog.Write(LogLevel.Debug, "Executing template...");
		await instance.ExecuteAsync().ConfigureAwait(false);
		string renderedTemplate = await instance.ResultAsync().ConfigureAwait(false);
		sLog.Write(LogLevel.Debug, "Executing template completed successfully.");

		return renderedTemplate;
	}

	private void EnableDebugging(string templateSource, string debuggingOutputDirectory = null)
	{
		string path = Path.Combine(debuggingOutputDirectory ?? ".", TemplateFileName);
		sLog.Write(LogLevel.Debug, "Debug-mode enabled. Writing PDB file ({0}).", path);
		File.WriteAllText(path, templateSource);
	}
}
