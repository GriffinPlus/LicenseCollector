namespace GriffinPlus.LicenseCollector
{
	/// <summary>
	/// Information of a 3rd party package and its license.
	/// </summary>
	public class PackageLicenseInfo
	{
		#region Construction

		/// <summary>
		/// Initializes a new instance of <see cref="PackageLicenseInfo"/> for a static license.
		/// </summary>
		/// <param name="id">Identifier of the package.</param>
		/// <param name="license">License of the package.</param>
		public PackageLicenseInfo(string id, string license) : this(id, "", "", "", "", license) { }


		/// <summary>
		/// Initializes a new instance of <see cref="PackageLicenseInfo"/> for a NuGet package.
		/// </summary>
		/// <param name="id">Identifier of the package.</param>
		/// <param name="version">Version of the package.</param>
		/// <param name="author">Author of the package.</param>
		/// <param name="licenseUrl">Url to license.</param>
		/// <param name="projectUrl">Url to project.</param>
		/// <param name="license">License of the package.</param>
		public PackageLicenseInfo(string id, string version, string author, string licenseUrl, string projectUrl, string license)
		{
			PackageIdentifier = id;
			PackageVersion = version;
			Author = author;
			LicenseUrl = licenseUrl;
			ProjectUrl = projectUrl;
			License = license;
		}

		#endregion

		#region Properties

		/// <summary>
		/// Gets and sets identifier of this package.
		/// </summary>
		public string PackageIdentifier { get; set; }

		/// <summary>
		/// Gets and sets version of this package.
		/// </summary>
		public string PackageVersion { get; set; }

		/// <summary>
		/// Gets and sets author of this package.
		/// </summary>
		public string Author { get; set; }

		/// <summary>
		/// Gets and sets url to the license.
		/// </summary>
		public string LicenseUrl { get; set; }

		/// <summary>
		/// Gets and sets url to the project related to this package.
		/// </summary>
		public string ProjectUrl { get; set; }

		/// <summary>
		/// Get and sets license to this package.
		/// </summary>
		public string License { get; set; }

		#endregion
	}
}
