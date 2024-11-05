using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace GriffinPlus.LicenseCollector.Razor;

static class ObjectExtensions
{
	/// <summary>
	/// Checks whether this object is of an anonymous type.
	/// </summary>
	/// <param name="obj">The object to check.</param>
	/// <returns>
	/// <c>true</c> if the object is of an anonymous type;<br/>
	/// otherwise <c>false</c>.
	/// </returns>
	internal static bool IsAnonymous(this object obj)
	{
		var type = obj.GetType();

		return Attribute.IsDefined(type, typeof(CompilerGeneratedAttribute), false) &&
		       type.IsGenericType &&
		       type.Name.Contains("AnonymousType") &&
		       (type.Name.StartsWith("<>", StringComparison.Ordinal) || type.Name.StartsWith("VB$", StringComparison.Ordinal)) &&
		       type.Attributes.HasFlag(TypeAttributes.NotPublic);
	}
}
