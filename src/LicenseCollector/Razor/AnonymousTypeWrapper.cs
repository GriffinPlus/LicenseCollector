using System.Collections;
using System.Dynamic;
using System.Linq;

namespace GriffinPlus.LicenseCollector.Razor;

/// <summary>
/// Class to wrap objects in an anonymous <see cref="DynamicObject"/>. <br/>
/// Note: All properties that are of simple types, e.g. primitive data types like
/// <see cref="int"/> or simple types like <see cref="string"/>, are not wrapped.
/// </summary>
public class AnonymousTypeWrapper : DynamicObject
{
	private readonly object mModel;

	/// <summary>
	/// Initialize an instance of <see cref="AnonymousTypeWrapper"/>.
	/// </summary>
	/// <param name="model">The underlying model to be wrapped.</param>
	public AnonymousTypeWrapper(object model)
	{
		mModel = model;
	}

	/// <summary>
	/// Returns the requests property from the anonymous model.
	/// </summary>
	/// <param name="binder">Information which property should be returned.</param>
	/// <param name="result">The value contained in the requested property.</param>
	/// <returns>
	/// <c>true</c> if a value can be returned;<br/>
	/// <c>false</c> if the requested property cannot be found and, thus, no value can be returned.
	/// </returns>
	public override bool TryGetMember(GetMemberBinder binder, out object result)
	{
		var propertyInfo = mModel.GetType().GetProperty(binder.Name);

		if (propertyInfo == null)
		{
			result = null;
			return false;
		}

		result = propertyInfo.GetValue(mModel, null);

		if (result == null)
		{
			return true;
		}

		if (result.IsAnonymous())
		{
			result = new AnonymousTypeWrapper(result);
		}

		if (result is IDictionary dictionary)
		{
			var keys = dictionary.Keys.Cast<object>().ToList();

			foreach (object key in keys.Where(key => dictionary[key].IsAnonymous()))
			{
				dictionary[key] = new AnonymousTypeWrapper(dictionary[key]);
			}
		}
		else if (result is IEnumerable enumerable and not string)
		{
			result = enumerable.Cast<object>()
				.Select(e => e.IsAnonymous() ? new AnonymousTypeWrapper(e) : e)
				.ToList();
		}


		return true;
	}
}
