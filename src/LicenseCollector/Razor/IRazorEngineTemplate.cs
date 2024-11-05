using System.Threading.Tasks;

namespace GriffinPlus.LicenseCollector.Razor;

/// <summary>
/// Interface of the base of a razor template rendered by the <see cref="IRazorEngineCompiler"/>.
/// </summary>
public interface IRazorEngineTemplate
{
	/// <summary>
	/// Helper class containing helper functions accessible in the template.
	/// </summary>
	TemplateHelper Helper { get; }

	/// <summary>
	/// The data-model to fill the gaps in the template.
	/// </summary>
	dynamic Model { get; set; }

	/// <summary>
	/// Indicates whether the template is HTML or regular text.
	/// </summary>
	bool IsHtml { get; set; }

	/// <summary>
	/// Adds the given literal to the result string.
	/// </summary>
	/// <param name="literal">The literal added to the result string.</param>
	void WriteLiteral(string literal = null);

	/// <summary>
	/// Adds the given object to the result string.
	/// </summary>
	/// <param name="obj">The object added to the result string.</param>
	void Write(object obj = null);

	/// <summary>
	/// Writes the specified attribute name to the result.
	/// </summary>
	/// <param name="name">The name.</param>
	/// <param name="prefix">The prefix.</param>
	/// <param name="prefixOffset">The prefix offset.</param>
	/// <param name="suffix">The suffix.</param>
	/// <param name="suffixOffset">The suffix offset</param>
	/// <param name="attributeValuesCount">The attribute values count.</param>
	void BeginWriteAttribute(
		string name,
		string prefix,
		int    prefixOffset,
		string suffix,
		int    suffixOffset,
		int    attributeValuesCount);

	/// <summary>
	/// Writes the specified attribute value to the result.
	/// </summary>
	/// <param name="prefix">The prefix.</param>
	/// <param name="prefixOffset">The prefix offset.</param>
	/// <param name="value">The value.</param>
	/// <param name="valueOffset">The value offset.</param>
	/// <param name="valueLength">The value length.</param>
	/// <param name="isLiteral">The is literal.</param>
	void WriteAttributeValue(
		string prefix,
		int    prefixOffset,
		object value,
		int    valueOffset,
		int    valueLength,
		bool   isLiteral);

	/// <summary>
	/// Writes the attribute end to the result.
	/// </summary>
	void EndWriteAttribute();

	/// <summary>
	/// Renders the template.
	/// Call <see cref="ResultAsync"/> to retrieve the rendered result.
	/// </summary>
	Task ExecuteAsync();

	/// <summary>
	/// Returns the rendered template.
	/// </summary>
	/// <returns>The rendered template.</returns>
	Task<string> ResultAsync();
}
