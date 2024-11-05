using System;
using System.Threading;
using System.Threading.Tasks;

namespace GriffinPlus.LicenseCollector.Razor;

/// <summary>
/// Interface describing a razor compiler that allows to compile and fill (run)
/// a template based on Razor.
/// </summary>
public interface IRazorEngineCompiler
{
	/// <summary>
	/// Checks whether a template with the specified key was cached already.
	/// </summary>
	/// <param name="templateKey">The identifier for the template to search in the cache.</param>
	/// <returns>
	/// <c>true</c> if the <paramref name="templateKey"/> was found in the cache;<br/>
	/// otherwise <c>false</c>.
	/// </returns>
	/// <exception cref="ArgumentNullException"><paramref name="templateKey"/> is <c>null</c>.</exception>
	bool IsTemplateCached(string templateKey);

	/// <summary>
	/// Compiles the specified template and stores the result in a template cache using the given <paramref name="templateKey"/>.
	/// </summary>
	/// <param name="templateKey">The identifier to be used to store the compiled template in the template cache.</param>
	/// <param name="templateSource">The template as string. Can be an HTML or plain-text template.</param>
	/// <param name="options">Additional options for compiling the template (it may be <c>null</c>).</param>
	/// <exception cref="ArgumentNullException"><paramref name="templateKey"/> is <c>null</c>.</exception>
	/// <exception cref="ArgumentException"><paramref name="templateSource"/> is null or empty.</exception>
	void Compile(
		string                        templateKey,
		string                        templateSource,
		RazorEngineCompilationOptions options = null);

	/// <summary>
	/// Asynchronously compiles the specified template and stores the result in a template cache using the given <paramref name="templateKey"/>.
	/// </summary>
	/// <param name="templateKey">The identifier to be used to store the compiled template in the template cache.</param>
	/// <param name="templateSource">The template as string. Can be an HTML or plain-text template.</param>
	/// <param name="options">Additional options for compiling the template (it may be <c>null</c>).</param>
	/// <param name="cancellationToken">Cancellation token that can be signaled to abort the operation.</param>
	/// <exception cref="ArgumentNullException"><paramref name="templateKey"/> is <c>null</c>.</exception>
	/// <exception cref="ArgumentException"><paramref name="templateSource"/> is <c>null</c> or empty.</exception>
	Task CompileAsync(
		string                        templateKey,
		string                        templateSource,
		RazorEngineCompilationOptions options           = null,
		CancellationToken             cancellationToken = default);

	/// <summary>
	/// Generates the template specified by its key and the given context.
	/// </summary>
	/// <param name="templateKey">The key identifying the template to render.</param>
	/// <param name="isHtml">
	/// <c>true</c> to render the template as HTML;<br/>
	/// <c>false</c> to render the template as regular text.
	/// </param>
	/// <param name="model">The anonymous model to fill the gaps in the template.</param>
	/// <returns>The rendered template as a string.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="templateKey"/> is <c>null</c>.</exception>
	string Run(
		string templateKey,
		bool   isHtml,
		object model = null);

	/// <summary>
	/// Asynchronously generates the template specified by its key and the given context.
	/// </summary>
	/// <param name="templateKey">The key identifying the template to render.</param>
	/// <param name="isHtml">
	/// <c>true</c> to render the template as HTML;<br/>
	/// <c>false</c> to render the template as regular text.
	/// </param>
	/// <param name="model">The anonymous model to fill the gaps in the template.</param>
	/// <param name="cancellationToken">Cancellation token that can be signaled to abort the operation.</param>
	/// <returns>The rendered template as a string.</returns>
	/// <exception cref="ArgumentNullException"><paramref name="templateKey"/> is <c>null</c>.</exception>
	Task<string> RunAsync(
		string            templateKey,
		bool              isHtml,
		object            model             = null,
		CancellationToken cancellationToken = default);
}
