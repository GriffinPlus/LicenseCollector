using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using GriffinPlus.Lib.Logging;
using GriffinPlus.Lib.Threading;

using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
#if NET7_0 || NET8_0
using System.Reflection.Metadata;
#endif

namespace GriffinPlus.LicenseCollector.Razor;

/// <summary>
/// Implementation of the Interface <see cref="IRazorEngineCompiler"/>.<br/>
/// It that allows to compile and fill (run) a template based on Razor.
/// </summary>
class RazorEngineCompiler : IRazorEngineCompiler
{
	private static readonly LogWriter sLog = LogWriter.Get<RazorEngineCompiler>();

	// internal template cache
	private readonly Dictionary<string, RazorEngineCompiledTemplate> mTemplates         = [];
	private readonly AsyncReaderWriterLock                           mTemplateCacheLock = new();

	public RazorEngineCompiler() { }

	/// <inheritdoc/>
	public bool IsTemplateCached(string templateKey)
	{
		if (templateKey == null) throw new ArgumentNullException(nameof(templateKey));

		using (mTemplateCacheLock.ReaderLock())
		{
			return mTemplates.ContainsKey(templateKey);
		}
	}

	/// <inheritdoc/>
	public string Run(
		string templateKey,
		bool   isHtml,
		object model = null)
	{
		return RunAsync(
				templateKey,
				isHtml,
				model)
			.WaitAndUnwrapException();
	}

	/// <inheritdoc/>
	public async Task<string> RunAsync(
		string            templateKey,
		bool              isHtml,
		object            model             = null,
		CancellationToken cancellationToken = default)
	{
		if (templateKey == null) throw new ArgumentNullException(nameof(templateKey));

		RazorEngineCompiledTemplate template;
		using (await mTemplateCacheLock.ReaderLockAsync(cancellationToken).ConfigureAwait(false))
		{
			if (!mTemplates.TryGetValue(templateKey, out template))
			{
				sLog.Write(LogLevel.Error, "Template ({0}) was not found in cache.", templateKey);
				return "";
			}
		}

		return await template.RunAsync(isHtml, model, cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc/>
	public void Compile(
		string                        templateKey,
		string                        templateSource,
		RazorEngineCompilationOptions options = null)
	{
		CompileAsync(
				templateKey,
				templateSource,
				options)
			.WaitAndUnwrapException();
	}

	/// <inheritdoc/>
	public async Task CompileAsync(
		string                        templateKey,
		string                        templateSource,
		RazorEngineCompilationOptions options           = null,
		CancellationToken             cancellationToken = default)
	{
		if (templateKey == null) throw new ArgumentNullException(nameof(templateKey));
		if (string.IsNullOrEmpty(templateSource)) throw new ArgumentException($"{nameof(templateSource)} must not be null or empty.", nameof(templateSource));

		sLog.Write(LogLevel.Debug, "Compiling template ({0})...", templateKey);

		options ??= new RazorEngineCompilationOptions();

		templateSource = WriteDirectives(templateSource, options);

		const string projectPath = ".";
		var engine = RazorProjectEngine.Create(
			RazorConfiguration.Default,
			RazorProjectFileSystem.Create(projectPath),
			builder =>
			{
				builder.SetNamespace(options.TemplateNamespace);
			});


		string fileName = string.IsNullOrWhiteSpace(options.TemplateFilename)
			                  ? Path.GetRandomFileName() + ".cshtml"
			                  : options.TemplateFilename;

		var document = RazorSourceDocument.Create(templateSource, fileName);
		sLog.Write(LogLevel.Debug, "Processing template with razor engine...");
		var codeDocument = engine.Process(
			document,
			null,
			new List<RazorSourceDocument>(),
			new List<TagHelperDescriptor>());
		sLog.Write(LogLevel.Debug, "Processed template with razor engine.");

		var razorCSharpDocument = codeDocument.GetCSharpDocument();
		var sourceText = SourceText.From(razorCSharpDocument.GeneratedCode, Encoding.UTF8);
		var syntaxTree = CSharpSyntaxTree.ParseText(sourceText, cancellationToken: cancellationToken);

		sLog.Write(LogLevel.Debug, "Compiling template...");
		var compilation = CSharpCompilation.Create(
			fileName,
			[
				syntaxTree
			],
			options.ReferencedAssemblies
				.Select(
					ass =>
					{
#if NET48
						return MetadataReference.CreateFromFile(ass.Location);
#elif NET7_0 || NET8_0
						unsafe
						{
							ass.TryGetRawMetadata(out byte* blob, out int length);
							var moduleMetadata = ModuleMetadata.CreateFromMetadata((IntPtr)blob, length);
							var assemblyMetadata = AssemblyMetadata.Create(moduleMetadata);
							var metadataReference = assemblyMetadata.GetReference();

							return metadataReference;
						}
#endif
					})
				.Concat(options.MetadataReferences)
				.ToList(),
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));


		var assemblyStream = new MemoryStream();
		var pdbStream = options.IncludeDebuggingInfo ? new MemoryStream() : null;

		var emitResult = compilation.Emit(assemblyStream, pdbStream, cancellationToken: cancellationToken);
		if (!emitResult.Success)
		{
			var exception = new RazorEngineCompilationException
			{
				Errors = emitResult.Diagnostics.ToList(),
				GeneratedCode = razorCSharpDocument.GeneratedCode
			};
			sLog.Write(LogLevel.Error, "Compiling template failed. Exception:\n{0}.", exception);
			throw exception;
		}

		var result = new RazorEngineCompiledTemplate(options.TemplateNamespace, assemblyStream.ToArray(), pdbStream?.ToArray(), fileName, templateSource);
		using (await mTemplateCacheLock.WriterLockAsync(cancellationToken).ConfigureAwait(false))
		{
			mTemplates[templateKey] = result;
		}

		sLog.Write(LogLevel.Debug, "Compiling template completed successfully.");
	}

	private string WriteDirectives(string content, RazorEngineCompilationOptions options)
	{
		sLog.Write(LogLevel.Debug, "Adding 'using' and 'inherits' definitions...");

		var stringBuilder = new StringBuilder();
		stringBuilder.AppendLine($"@inherits {options.Inherits}");

		foreach (string entry in options.DefaultUsings)
		{
			stringBuilder.AppendLine($"@using {entry}");
		}

		stringBuilder.Append(content);

		return stringBuilder.ToString();
	}
}
