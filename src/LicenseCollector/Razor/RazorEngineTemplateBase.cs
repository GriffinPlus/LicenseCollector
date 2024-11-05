using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace GriffinPlus.LicenseCollector.Razor;

/// <summary>
/// Template-implementation of <see cref="IRazorEngineTemplate"/>.<br/>
/// It is used as base class for all compiled templates of the <see cref="RazorEngineCompiler"/>.
/// </summary>
public abstract class RazorEngineTemplateBase : IRazorEngineTemplate
{
	private readonly StringBuilder mStringBuilder   = new();
	private          string        mAttributeSuffix = null;

	/// <inheritdoc/>
	public TemplateHelper Helper { get; } = new();

	/// <inheritdoc/>
	public dynamic Model { get; set; }

	/// <inheritdoc/>
	public bool IsHtml { get; set; }

	/// <inheritdoc/>
	public virtual void WriteLiteral(string literal = null)
	{
		mStringBuilder.Append(literal);
	}

	/// <inheritdoc/>
	public virtual void Write(object obj = null)
	{
		string data = IsHtml ? WebUtility.HtmlEncode(obj?.ToString()) : obj?.ToString();
		mStringBuilder.Append(data);
	}

	/// <inheritdoc/>
	public virtual void BeginWriteAttribute(
		string name,
		string prefix,
		int    prefixOffset,
		string suffix,
		int    suffixOffset,
		int    attributeValuesCount)
	{
		mAttributeSuffix = suffix;
		mStringBuilder.Append(prefix);
	}

	/// <inheritdoc/>
	public virtual void WriteAttributeValue(
		string prefix,
		int    prefixOffset,
		object value,
		int    valueOffset,
		int    valueLength,
		bool   isLiteral)
	{
		mStringBuilder.Append(prefix);
		mStringBuilder.Append(value);
	}

	/// <inheritdoc/>
	public virtual void EndWriteAttribute()
	{
		mStringBuilder.Append(mAttributeSuffix);
		mAttributeSuffix = null;
	}

	/// <inheritdoc/>
	public virtual Task ExecuteAsync()
	{
		return Task.CompletedTask;
	}

	/// <inheritdoc/>
	public virtual Task<string> ResultAsync()
	{
		return Task.FromResult(mStringBuilder.ToString());
	}
}
