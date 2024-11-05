using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;

using Microsoft.CodeAnalysis;

namespace GriffinPlus.LicenseCollector.Razor;

/// <summary>
/// Options for the template compilation process.
/// </summary>
public class RazorEngineCompilationOptions
{
	/// <summary>
	/// Assemblies important for the compilation.
	/// </summary>
	public HashSet<Assembly> ReferencedAssemblies { get; set; }

	/// <summary>
	/// Assemblies important for compilation process, but provided as <see cref="MetadataReferences"/>.
	/// </summary>
	public HashSet<MetadataReference> MetadataReferences { get; set; } = [];

	/// <summary>
	/// Namespace of the generated template.
	/// </summary>
	public string TemplateNamespace { get; set; } = "TemplateNamespace";

	/// <summary>
	/// Filename of the generated template.
	/// </summary>
	public string TemplateFilename { get; set; } = "";

	/// <summary>
	/// Parent class.
	/// </summary>
	public string Inherits { get; set; } = typeof(RazorEngineTemplateBase).ToString();

	/// <summary>
	/// Set to <c>true</c> to generate PDB symbols information along with the assembly for debugging support.
	/// </summary>
	public bool IncludeDebuggingInfo { get; set; } = false;

	/// <summary>
	/// Default usings injected into the template before compiling.
	/// </summary>
	public HashSet<string> DefaultUsings { get; set; } =
	[
		"System.Linq",
		"System.Collections",
		"System.Collections.Generic"
	];

	/// <summary>
	/// Initializes an object of <see cref="RazorEngineCompilationOptions"/>.
	/// </summary>
	public RazorEngineCompilationOptions()
	{
		bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
		bool isFullFramework = RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.OrdinalIgnoreCase);

		if (isWindows && isFullFramework)
		{
			ReferencedAssemblies =
			[
				typeof(object).Assembly,
				Assembly.Load(new AssemblyName("Microsoft.CSharp, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")),
				typeof(RazorEngineTemplateBase).Assembly,
				typeof(GCSettings).Assembly,
				typeof(IList).Assembly,
				typeof(IEnumerable<>).Assembly,
				typeof(Enumerable).Assembly,
				typeof(Expression).Assembly,
				Assembly.Load(new AssemblyName("netstandard, Version=2.0.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51"))
			];
		}

		if ((isWindows && !isFullFramework) || !isWindows) // i.e. NETCore
		{
			ReferencedAssemblies =
			[
				typeof(object).Assembly,
				Assembly.Load(new AssemblyName("Microsoft.CSharp")),
				typeof(RazorEngineTemplateBase).Assembly,
				Assembly.Load(new AssemblyName("System.Runtime")),
				typeof(IList).Assembly,
				typeof(IEnumerable<>).Assembly,
				Assembly.Load(new AssemblyName("System.Linq")),
				Assembly.Load(new AssemblyName("System.Linq.Expressions")),
				Assembly.Load(new AssemblyName("netstandard"))
			];
		}
	}
}
