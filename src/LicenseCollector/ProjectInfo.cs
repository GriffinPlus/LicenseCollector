namespace GriffinPlus.LicenseCollector
{

	/// <summary>
	/// Lists different supported project types
	/// </summary>
	public enum NuGetPackageDependency
	{
		/// <summary>
		/// Defines that project uses 'project.assets.json'.
		/// </summary>
		PackageReference,

		/// <summary>
		/// Defines that project uses 'packages.config'.
		/// </summary>
		PackagesConfig,

		/// <summary>
		/// No 'packages.config' or 'project.assets.json' found.
		/// </summary>
		NoDependencies
	}

	/// <summary>
	/// Information of a MSBuild project.
	/// </summary>
	public class ProjectInfo
	{
		#region Construction

		/// <summary>
		/// Creates a new instance of <see cref="ProjectInfo"/>.
		/// </summary>
		/// <param name="name">Name of the project.</param>
		/// <param name="absolutePath">Absolute path to the project file.</param>
		/// <param name="nugetInfo">Path to the NuGet information.</param>
		/// <param name="type">Used NuGet style.</param>
		public ProjectInfo(
			string                 name,
			string                 absolutePath,
			string                 nugetInfo,
			NuGetPackageDependency type)
		{
			ProjectName = name;
			ProjectAbsolutePath = absolutePath;
			NuGetInformationPath = nugetInfo;
			Type = type;
		}

		#endregion

		#region Properties

		/// <summary>
		/// Gets name of the project.
		/// </summary>
		public string ProjectName { get; }

		/// <summary>
		/// Gets absolute path to the project file.
		/// </summary>
		public string ProjectAbsolutePath { get; }

		/// <summary>
		/// Gets path to the NuGet information. The 'project.assets.json' for <see cref="NuGetPackageDependency.PackageReference"/>
		/// and the 'packages.config' for <see cref="NuGetPackageDependency.PackagesConfig"/>.
		/// </summary>
		public string NuGetInformationPath { get; }

		/// <summary>
		/// Gets type of project.
		/// </summary>
		public NuGetPackageDependency Type { get; }

		#endregion

		/// <summary>
		/// Returns the name of the project.
		/// </summary>
		/// <returns></returns>
		public override string ToString()
		{
			return ProjectName;
		}
	}

}
