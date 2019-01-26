using System;

namespace ZetaGlestInstaller {
	/// <summary>
	/// Holds the configuration parameters loaded from the application configuration file
	/// </summary>
	public class ConfigParams {
		/// <summary>
		/// The ZetaGlest version that this installer installs
		/// </summary>
		public readonly string Version;
		/// <summary>
		/// The url from where the binaries zip file is downloaded
		/// </summary>
		public readonly Uri BinariesUrl;
		/// <summary>
		/// The root directory inside the binaries zip file
		/// </summary>
		public readonly string BinariesDir;
		/// <summary>
		/// The binaries zip file MD5 hash
		/// </summary>
		public readonly string BinariesMD5;
		/// <summary>
		/// The url from where the data zip file is downloaded
		/// </summary>
		public readonly Uri DataUrl;
		/// <summary>
		/// The root directory inside the data zip file
		/// </summary>
		public readonly string DataDir;
		/// <summary>
		/// The data zip file MD5 hash
		/// </summary>
		public readonly string DataMD5;
		/// <summary>
		/// The number of bytes in the data zip file
		/// </summary>
		public readonly long DataBytes;
		/// <summary>
		/// The target output line count of 7z
		/// </summary>
		public readonly int Data7zLineCount;

		/// <summary>
		/// Initializes a new configuration
		/// </summary>
		/// <param name="version">The ZetaGlest version that this installer installs</param>
		/// <param name="binariesUrl">The url from where the binaries zip file is downloaded</param>
		/// <param name="binariesDir">The root directory inside the binaries zip file</param>
		/// <param name="binariesMD5">The binaries zip file MD5 hash</param>
		/// <param name="dataUrl">The url from where the data zip file is downloaded</param>
		/// <param name="dataDir">The root directory inside the data zip file</param>
		/// <param name="dataMD5">The data zip file MD5 hash</param>
		/// <param name="dataBytes">The number of bytes in the data zip file</param>
		/// <param name="data7zLineCount">The target output line count of 7z</param>
		public ConfigParams(string version, Uri binariesUrl, string binariesDir, string binariesMD5, Uri dataUrl, string dataDir, string dataMD5, long dataBytes, int data7zLineCount) {
			Version = version;
			BinariesUrl = binariesUrl;
			BinariesDir = binariesDir;
			BinariesMD5 = binariesMD5;
			DataUrl = dataUrl;
			DataDir = dataDir;
			DataMD5 = dataMD5;
			DataBytes = dataBytes;
			Data7zLineCount = data7zLineCount;
		}
	}
}