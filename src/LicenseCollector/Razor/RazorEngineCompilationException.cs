using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;

namespace GriffinPlus.LicenseCollector.Razor;

/// <summary>
/// Exception thrown during the compilation process of a razor template.
/// </summary>
public class RazorEngineCompilationException : Exception
{
	/// <summary>
	/// Initializes an object of <see cref="RazorEngineCompilationException"/>.
	/// </summary>
	public RazorEngineCompilationException() { }

	/// <summary>
	/// Initializes an object of <see cref="RazorEngineCompilationException"/>.
	/// </summary>
	/// <param name="innerException">
	/// The exception that is the cause of the current exception,
	/// or a null reference if no inner exception is specified.
	/// </param>
	public RazorEngineCompilationException(Exception innerException) : base(null, innerException) { }

	/// <summary>
	/// List of all errors.
	/// </summary>
	public List<Diagnostic> Errors { get; set; }

	/// <summary>
	/// The code generated during the compilation up to the point when the error occurred.
	/// </summary>
	public string GeneratedCode { get; set; }

	/// <summary>
	/// Generates a string message containing all error messages stored in <see cref="Errors"/>.
	/// </summary>
	public override string Message
	{
		get
		{
			string errors = Errors != null ? string.Join("\n", Errors.Where(w => w.IsWarningAsError || w.Severity == DiagnosticSeverity.Error)) : "";
			return "Unable to compile template: " + errors;
		}
	}
}
