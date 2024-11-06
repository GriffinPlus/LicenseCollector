using System.Collections.Generic;
using System.Text;

namespace GriffinPlus.LicenseCollector;

/// <summary>
/// Helper functions that are accessible in razor template files
/// (it can be used by specifying @Helper in the template).
/// </summary>
public class TemplateHelper
{
	/// <summary>
	/// Splits a long message into multiple lines ensuring that a line does not exceed
	/// the specified number of characters.
	/// </summary>
	/// <param name="message">Message to split.</param>
	/// <param name="maxLineLength">Maximum number of characters per line.</param>
	/// <returns>The message split into multiple lines.</returns>
	public static IEnumerable<string> SplitToLines(string message, int maxLineLength)
	{
		string[] words = message.Split(' ');
		var line = new StringBuilder();
		foreach (string word in words)
		{
			if (word.Length + line.Length <= maxLineLength)
			{
				line.Append(word + " ");
			}
			else
			{
				if (line.Length > 0)
				{
					yield return line.ToString().Trim();
					line.Clear();
				}

				string overflow = word;
				while (overflow.Length > maxLineLength)
				{
					yield return overflow[..maxLineLength];
					overflow = overflow[maxLineLength..];
				}

				line.Append(overflow + " ");
			}
		}

		yield return line.ToString().Trim();
	}
}
