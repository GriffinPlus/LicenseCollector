namespace GriffinPlus.LicenseCollector
{
	/// <summary>
	/// Lists different supported project types
	/// </summary>
	public enum ProjectType
	{
		/// <summary>
		/// Defines a C# project
		/// </summary>
		CsProject,
		/// <summary>
		/// Defines a C++ project
		/// </summary>
		CppProject
	}

	/// <summary>
	/// Information of a MSBuild project.
	/// </summary>
	public class ProjectInfo
	{
		#region Construction

		/// <summary>
		/// Creates a new instance of <see cref="ProjectInfo"/> of <see cref="ProjectType.CppProject"/>.
		/// </summary>
		/// <param name="name">Name of the project.</param>
		/// <param name="absolutePath">Absolute path to the project file.</param>
		public ProjectInfo(string name, string absolutePath) : this(name, absolutePath, null)
		{
			Type = ProjectType.CppProject;
		}

		/// <summary>
		/// Creates a new instance of <see cref="ProjectInfo"/> of <see cref="ProjectType.CsProject"/>.
		/// </summary>
		/// <param name="name">Name of the project.</param>
		/// <param name="absolutePath">Absolute path to the project file.</param>
		/// <param name="baseIntermediateOutputPath">Path of the property 'BaseIntermediateOutputPath'.</param>
		public ProjectInfo(string name, string absolutePath, string baseIntermediateOutputPath)
		{
			ProjectName = name;
			ProjectAbsolutePath = absolutePath;
			ProjectBaseIntermediateOutputPath = baseIntermediateOutputPath;
			Type = ProjectType.CsProject;
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
		/// Gets property 'BaseIntermediateOutputPath' of the project.
		/// </summary>
		public string ProjectBaseIntermediateOutputPath { get; }

		/// <summary>
		/// Gets type of project.
		/// </summary>
		public ProjectType Type { get; }

		#endregion
	}
}
